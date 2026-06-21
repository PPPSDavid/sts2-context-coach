using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Linq;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Logging;
using Sts2ContextCoach.Data;
using Sts2ContextCoach.Localization;
using Sts2ContextCoach.State;
using Sts2ContextCoach.Telemetry;

namespace Sts2ContextCoach.Llm;

/// <summary>Debounced batched OpenRouter/OpenAI-compatible chat calls for shop + card-reward rows.</summary>
public static class LlmBatchCoordinator
{
    private const int DebounceMs = 120;
    private static int _missingApiKeyLogs;

    private static readonly object Gate = new();
    /// <summary>No global cap — each request uses <see cref="ContextCoachConfig.EffectiveLlmTimeoutSeconds"/> via <see cref="CancellationTokenSource"/>.</summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int _scheduleVersion;
    private static long _requestSeq;

    /// <summary>When the current <see cref="_uiBatchKey"/> was first set to Pending (for stall watchdog).</summary>
    private static long _pendingSinceTicks;

    private static string? _uiBatchKey;
    private static LlmOverlayBatchStatus _status = LlmOverlayBatchStatus.Idle;
    private static string? _lastError;

    private static Dictionary<string, LlmCardAdvice> _adviceByKey = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<LlmCoachCandidate>? _lastCandidates;

    /// <summary>Last batch that completed successfully; used once when transitioning to a new batch key.</summary>
    private static string? _lastGoodBatchKey;

    private static Dictionary<string, int>? _lastGoodDeckSnapshot;
    private static HashSet<string>? _lastGoodCandidateNames;
    private static string? _lastGoodBatchSummary;
    private static long _lastShopScheduleTicks;
    private static LlmDeckProfile? _deckProfile;
    private static string? _deckProfilePendingSignature;
    private static long _deckProfileSeq;

    public static string ComputeBatchKey(string decisionType, GameState state, IReadOnlyList<LlmCoachCandidate> candidates)
    {
        var tail = string.Join(
            ",",
            candidates
                .Select(c => c.BatchStableKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase));
        var key = $"{decisionType}|c{state.Character ?? "?"}|{tail}";
        // Re-run coach when deck changes (e.g. after buying a shop or reward card).
        if (string.Equals(decisionType, "shop", StringComparison.Ordinal) ||
            string.Equals(decisionType, "card_reward", StringComparison.Ordinal))
        {
            key += "|deck" + DeckContentSignature(state.Deck);
            // Shop: do NOT append gold. GetStateForCard fills Gold via TryResolveHudPlayerGold(anchor); that walk
            // succeeds from some merchant slots and fails from others → alternating batch keys → every overlay tick
            // supersedes the in-flight HTTP request → Pending forever, no timeout, no results.
        }

        return key;
    }

    private static string DeckContentSignature(IReadOnlyList<CardInstance>? deck)
    {
        if (deck == null || deck.Count == 0) return "0";
        unchecked
        {
            var h = new HashCode();
            h.Add(deck.Count);
            foreach (var c in deck
                         .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                         .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Upgraded))
            {
                h.Add(StringComparer.OrdinalIgnoreCase.GetHashCode(c.Name));
                h.Add(c.Upgraded);
            }

            return h.ToHashCode().ToString("X8");
        }
    }

    internal static string ComputeDeckProfileSignature(GameState state)
    {
        unchecked
        {
            var h = new HashCode();
            h.Add(state.Character ?? "?");
            h.Add(state.Act ?? 0);
            h.Add(state.Ascension ?? 0);

            var deck = state.Deck ?? [];
            h.Add(deck.Count);
            foreach (var c in deck
                         .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                         .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Upgraded))
            {
                h.Add(StringComparer.OrdinalIgnoreCase.GetHashCode(c.Name));
                h.Add(c.Upgraded);
            }

            var relics = state.Relics ?? [];
            h.Add(relics.Count);
            foreach (var r in relics.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                h.Add(StringComparer.OrdinalIgnoreCase.GetHashCode(r));

            return deck.Count == 0 ? "0" : h.ToHashCode().ToString("X8");
        }
    }

    /// <summary>Called from main thread when a row of candidates should be scored together.</summary>
    public static void ScheduleBatch(string decisionType, GameState globalState, IReadOnlyList<LlmCoachCandidate> candidates)
    {
        if (!ContextCoachConfig.IsLlmScoringEnabled)
            return;

        if (ContextCoachConfig.TryGetLlmApiKey() == null)
        {
            if (Interlocked.Increment(ref _missingApiKeyLogs) <= 4)
            {
                Log.Warn("[ContextCoach][LLM] scoring_mode=llm but API key env is empty; set " +
                         ContextCoachConfig.Current.LlmApiKeyEnv + " or switch scoring_mode to heuristic.");
            }

            return;
        }

        if (candidates.Count == 0)
            return;

        var batchKey = ComputeBatchKey(decisionType, globalState, candidates);
        var corr = Guid.NewGuid().ToString("N")[..8];

        IReadOnlyDictionary<string, int>? inferDeckBefore = null;
        IReadOnlyCollection<string>? inferNames = null;
        string? inferSummary = null;

        lock (Gate)
        {
            var nowTicks = Environment.TickCount64;
            var inMerchantScreen =
                (globalState.CurrentScreen?.Contains("merchant", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (globalState.CurrentScreen?.Contains("shop", StringComparison.OrdinalIgnoreCase) ?? false);
            // In merchant scenes we can still see stray non-shop card overlays (e.g. hidden reward/preview cards).
            // Those "card_reward" schedules can supersede the active shop batch forever; ignore them briefly after
            // any shop schedule so merchant advice can settle.
            if (inMerchantScreen && !string.Equals(decisionType, "shop", StringComparison.Ordinal))
            {
                return;
            }
            if (!string.Equals(decisionType, "shop", StringComparison.Ordinal) &&
                _lastShopScheduleTicks != 0 &&
                nowTicks - _lastShopScheduleTicks < 5000)
            {
                return;
            }
            if (string.Equals(decisionType, "shop", StringComparison.Ordinal))
                _lastShopScheduleTicks = nowTicks;

            TryScheduleDeckProfileSummaryLocked(globalState);

            if (string.Equals(_uiBatchKey, batchKey, StringComparison.Ordinal) &&
                (_status == LlmOverlayBatchStatus.Ready || _status == LlmOverlayBatchStatus.Pending ||
                 _status == LlmOverlayBatchStatus.Failed))
            {
                return;
            }

            if (_lastGoodBatchKey != null &&
                !string.Equals(_lastGoodBatchKey, batchKey, StringComparison.Ordinal) &&
                _lastGoodDeckSnapshot != null &&
                _lastGoodCandidateNames is { Count: > 0 })
            {
                inferDeckBefore = new Dictionary<string, int>(_lastGoodDeckSnapshot, StringComparer.OrdinalIgnoreCase);
                inferNames = new HashSet<string>(_lastGoodCandidateNames, StringComparer.OrdinalIgnoreCase);
                inferSummary = _lastGoodBatchSummary;
                _lastGoodBatchKey = null;
                _lastGoodDeckSnapshot = null;
                _lastGoodCandidateNames = null;
                _lastGoodBatchSummary = null;
            }
        }

        if (inferDeckBefore != null && inferNames != null && inferNames.Count > 0)
            CoachPickHistory.TryInferPick(globalState.Deck, inferDeckBefore, inferNames, inferSummary);

        lock (Gate)
        {
            if (string.Equals(_uiBatchKey, batchKey, StringComparison.Ordinal) &&
                (_status == LlmOverlayBatchStatus.Ready || _status == LlmOverlayBatchStatus.Pending ||
                 _status == LlmOverlayBatchStatus.Failed))
            {
                return;
            }

            var ver = ++_scheduleVersion;
            var keyChanged = _uiBatchKey is null || !string.Equals(_uiBatchKey, batchKey, StringComparison.Ordinal);
            _uiBatchKey = batchKey;
            _status = LlmOverlayBatchStatus.Pending;
            _lastError = null;
            if (keyChanged)
                _pendingSinceTicks = Environment.TickCount64;

            Log.Info($"[ContextCoach][LLM] schedule corr={corr} batchKey={batchKey} candidates={candidates.Count} debounce={DebounceMs}ms");

            _ = RunDebouncedAsync(ver, corr, decisionType, batchKey, globalState, candidates);
        }
    }

    private static void TryScheduleDeckProfileSummaryLocked(GameState state)
    {
        if (!ContextCoachConfig.Current.LlmEnableDeckProfileSummary)
            return;

        var decision = ComputeDeckProfileRefreshDecision(state, _deckProfile, _deckProfilePendingSignature);
        if (!decision.ShouldSchedule)
            return;

        var seq = Interlocked.Increment(ref _deckProfileSeq);
        var corr = Guid.NewGuid().ToString("N")[..8];
        _deckProfilePendingSignature = decision.Signature;
        Log.Info($"[ContextCoach][LLM] deck-summary schedule corr={corr} sig={decision.Signature} reason={decision.Reason}");

        var snapshot = CloneStateForDeckSummary(state);
        _ = RunDeckProfileSummaryAsync(seq, corr, decision.Signature, snapshot);
    }

    internal static LlmDeckProfileRefreshDecision ComputeDeckProfileRefreshDecision(
        GameState state,
        LlmDeckProfile? currentProfile,
        string? pendingSignature)
    {
        var signature = ComputeDeckProfileSignature(state);
        if (signature == "0")
            return new LlmDeckProfileRefreshDecision { ShouldSchedule = false, Signature = signature, Reason = "empty_deck" };

        if (string.Equals(pendingSignature, signature, StringComparison.Ordinal))
            return new LlmDeckProfileRefreshDecision { ShouldSchedule = false, Signature = signature, Reason = "pending_same_signature" };

        if (currentProfile == null)
            return new LlmDeckProfileRefreshDecision { ShouldSchedule = true, Signature = signature, Reason = "cold_start" };

        if (!string.Equals(currentProfile.Signature, signature, StringComparison.Ordinal))
            return new LlmDeckProfileRefreshDecision { ShouldSchedule = true, Signature = signature, Reason = "signature_changed" };

        var floorDelta = Math.Abs((state.Floor ?? 0) - (currentProfile.GeneratedFloor ?? 0));
        if (floorDelta >= ContextCoachConfig.EffectiveLlmDeckProfileRefreshFloorDelta)
            return new LlmDeckProfileRefreshDecision { ShouldSchedule = true, Signature = signature, Reason = "floor_advanced" };

        return new LlmDeckProfileRefreshDecision { ShouldSchedule = false, Signature = signature, Reason = "fresh" };
    }

    private static GameState CloneStateForDeckSummary(GameState state)
    {
        return new GameState
        {
            Character = state.Character,
            Act = state.Act,
            Floor = state.Floor,
            Ascension = state.Ascension,
            MaxEnergy = state.MaxEnergy,
            Hp = state.Hp,
            MaxHp = state.MaxHp,
            Gold = state.Gold,
            Deck = state.Deck?.Select(c => new CardInstance { Name = c.Name, Upgraded = c.Upgraded }).ToList(),
            Relics = state.Relics?.ToList()
        };
    }

    private static async Task RunDebouncedAsync(
        int version,
        string corr,
        string decisionType,
        string batchKey,
        GameState globalState,
        IReadOnlyList<LlmCoachCandidate> candidates)
    {
        try
        {
            await Task.Delay(DebounceMs).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var superseded = false;
        lock (Gate)
        {
            if (version != _scheduleVersion)
                superseded = true;
        }

        if (superseded)
        {
            Log.Info($"[ContextCoach][LLM] debounce superseded corr={corr} (version mismatch)");
            var modelDeb = string.IsNullOrWhiteSpace(ContextCoachConfig.Current.LlmModel)
                ? "openai/gpt-4o-mini"
                : ContextCoachConfig.Current.LlmModel.Trim();
            RunLogger.LogLlmCoachBatch(
                globalState,
                corr,
                decisionType,
                batchKey,
                modelDeb,
                0,
                "debounce_superseded",
                null,
                "version mismatch after debounce",
                null);
            return;
        }

        var seq = Interlocked.Increment(ref _requestSeq);
        try
        {
            await ExecuteHttpAsync(seq, corr, decisionType, batchKey, globalState, candidates).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] batch failed corr={corr}: {ex.Message}");
            lock (Gate)
            {
                if (seq != _requestSeq)
                    return;
                _status = LlmOverlayBatchStatus.Failed;
                _lastError = ex.Message;
                _pendingSinceTicks = 0;
                _adviceByKey = new Dictionary<string, LlmCardAdvice>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static async Task RunDeckProfileSummaryAsync(long seq, string corr, string signature, GameState state)
    {
        try
        {
            var profile = await ExecuteDeckProfileHttpAsync(corr, signature, state).ConfigureAwait(false);
            lock (Gate)
            {
                if (seq != _deckProfileSeq)
                    return;
                _deckProfile = profile;
                _deckProfilePendingSignature = null;
            }

            Log.Info($"[ContextCoach][LLM] deck-summary ok corr={corr} sig={signature} floor={profile.GeneratedFloor?.ToString() ?? "?"}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] deck-summary failed corr={corr} sig={signature}: {ex.Message}");
            lock (Gate)
            {
                if (seq != _deckProfileSeq)
                    return;
                _deckProfilePendingSignature = null;
            }
        }
    }

    private static async Task ExecuteHttpAsync(
        long seq,
        string corr,
        string decisionType,
        string batchKey,
        GameState globalState,
        IReadOnlyList<LlmCoachCandidate> candidates)
    {
        var model = string.IsNullOrWhiteSpace(ContextCoachConfig.Current.LlmModel)
            ? "openai/gpt-4o-mini"
            : ContextCoachConfig.Current.LlmModel.Trim();
        var apiKey = ContextCoachConfig.TryGetLlmApiKey();
        if (apiKey == null)
        {
            lock (Gate)
            {
                if (seq != _requestSeq) return;
                _status = LlmOverlayBatchStatus.Failed;
                _lastError = "API key missing";
                _pendingSinceTicks = 0;
            }

            RunLogger.LogLlmCoachBatch(
                globalState, corr, decisionType, batchKey, model, 0,
                "api_key_missing", null, "API key missing", null);
            return;
        }

        var deckCounts = CountDeck(globalState.Deck);
        var candidateNameSet = candidates.Select(c => c.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchSummary = $"{decisionType} [{string.Join(", ", candidates.Select(c => c.InternalName))}]";

        var timeoutSec = ContextCoachConfig.EffectiveLlmTimeoutSeconds;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

        var baseUrl = (ContextCoachConfig.Current.LlmBaseUrl ?? "https://openrouter.ai/api/v1").TrimEnd('/');
        var url = baseUrl + "/chat/completions";

        var system = BuildSystemPrompt(LocalizationManager.Language);
        var (user, keywordHintCount) = BuildUserPayload(decisionType, globalState, candidates);
        if (keywordHintCount > 0)
            Log.Info($"[ContextCoach][LLM] user payload includes keyword_hints: {keywordHintCount} term(s) from data/keywords.json");

        var maxTok = Math.Clamp(1400 + candidates.Count * 280, 2200, 6000);
        var body = new ChatCompletionRequest
        {
            Model = model,
            Temperature = 0.35,
            MaxTokens = maxTok,
            ResponseFormat = ContextCoachConfig.Current.LlmJsonObjectResponseFormat
                ? new LlmJsonResponseFormat()
                : null,
            Messages =
            [
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = user }
            ]
        };

        var json = JsonSerializer.Serialize(body, JsonOpt);
        var reqBytes = Encoding.UTF8.GetByteCount(json);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Sts2ContextCoach/0.1 (SlayTheSpire2-mod)");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var referer = ContextCoachConfig.Current.LlmHttpReferer;
        if (!string.IsNullOrWhiteSpace(referer))
            req.Headers.TryAddWithoutValidation("HTTP-Referer", referer.Trim());
        var title = ContextCoachConfig.Current.LlmAppTitle;
        if (!string.IsNullOrWhiteSpace(title))
            req.Headers.TryAddWithoutValidation("X-Title", title.Trim());
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        Log.Info($"[ContextCoach][LLM] POST corr={corr} model={model} timeout={timeoutSec}s bytes={reqBytes}");

        string? transcriptBasename = null;
        try
        {
            string raw;
            try
            {
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    Log.Warn($"[ContextCoach][LLM] HTTP {(int)resp.StatusCode} corr={corr} body_head={Truncate(raw, 400)}");
                    throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                }
            }
            catch (OperationCanceledException oce) when (cts.IsCancellationRequested)
            {
                Log.Warn($"[ContextCoach][LLM] timed out corr={corr} after {timeoutSec}s ({oce.GetType().Name})");
                throw new InvalidOperationException($"LLM request timed out after {timeoutSec}s.", oce);
            }

            var content = ExtractAssistantContent(raw);
            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Warn($"[ContextCoach][LLM] empty assistant content corr={corr} body_head={Truncate(raw, 400)}");
                throw new InvalidOperationException("Empty assistant message");
            }

            transcriptBasename = LlmTranscriptLogger.TryWrite(corr, model, system, user, raw, content);

            var parsed = ParseRankings(content, candidates);
            if (parsed.Count == 0)
            {
                Log.Warn($"[ContextCoach][LLM] parse produced 0 rows corr={corr} content_head={Truncate(content, 500)}");
                throw new InvalidOperationException("LLM JSON did not match any candidate");
            }

            var topTelemetry = BuildCoachTopTelemetry(parsed, candidates);

            lock (Gate)
            {
                if (seq != _requestSeq)
                {
                    Log.Info($"[ContextCoach][LLM] drop stale response corr={corr}");
                    RunLogger.LogLlmCoachBatch(
                        globalState,
                        corr,
                        decisionType,
                        batchKey,
                        model,
                        reqBytes,
                        "stale_response",
                        transcriptBasename,
                        null,
                        topTelemetry);
                    return;
                }

                _adviceByKey = parsed;
                _lastCandidates = candidates;
                _status = LlmOverlayBatchStatus.Ready;
                _lastError = null;
                _lastGoodBatchKey = batchKey;
                _lastGoodDeckSnapshot = new Dictionary<string, int>(deckCounts, StringComparer.OrdinalIgnoreCase);
                _lastGoodCandidateNames = candidateNameSet;
                _lastGoodBatchSummary = batchSummary;
                _pendingSinceTicks = 0;
            }

            var ranked = ToRankedList(parsed, candidates);
            CoachPickHistory.AppendVerdict(decisionType, batchSummary, ranked);
            Log.Info($"[ContextCoach][LLM] ok corr={corr} parsed={parsed.Count} top={(ranked.Count > 0 ? ranked[0].name : "?")}");
            RunLogger.LogLlmCoachBatch(
                globalState,
                corr,
                decisionType,
                batchKey,
                model,
                reqBytes,
                "ok",
                transcriptBasename,
                null,
                topTelemetry);
        }
        catch (Exception ex)
        {
            RunLogger.LogLlmCoachBatch(
                globalState,
                corr,
                decisionType,
                batchKey,
                model,
                reqBytes,
                ClassifyCoachBatchException(ex),
                transcriptBasename,
                ex.Message,
                null);
            throw;
        }
    }

    private static async Task<LlmDeckProfile> ExecuteDeckProfileHttpAsync(string corr, string signature, GameState state)
    {
        var model = string.IsNullOrWhiteSpace(ContextCoachConfig.Current.LlmModel)
            ? "openai/gpt-4o-mini"
            : ContextCoachConfig.Current.LlmModel.Trim();
        var apiKey = ContextCoachConfig.TryGetLlmApiKey();
        if (apiKey == null)
        {
            RunLogger.LogLlmDeckSummary(state, corr, signature, model, 0, "api_key_missing", null, "API key missing");
            throw new InvalidOperationException("API key missing");
        }

        var timeoutSec = ContextCoachConfig.EffectiveLlmTimeoutSeconds;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

        var baseUrl = (ContextCoachConfig.Current.LlmBaseUrl ?? "https://openrouter.ai/api/v1").TrimEnd('/');
        var url = baseUrl + "/chat/completions";

        var system = BuildDeckSummarySystemPrompt(LocalizationManager.Language);
        var user = BuildDeckSummaryUserPayload(state);
        var body = new ChatCompletionRequest
        {
            Model = model,
            Temperature = 0.2,
            MaxTokens = 720,
            ResponseFormat = ContextCoachConfig.Current.LlmJsonObjectResponseFormat
                ? new LlmJsonResponseFormat()
                : null,
            Messages =
            [
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = user }
            ]
        };

        var json = JsonSerializer.Serialize(body, JsonOpt);
        var reqBytes = Encoding.UTF8.GetByteCount(json);
        string? transcriptBasename = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Sts2ContextCoach/0.1 (SlayTheSpire2-mod)");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var referer = ContextCoachConfig.Current.LlmHttpReferer;
            if (!string.IsNullOrWhiteSpace(referer))
                req.Headers.TryAddWithoutValidation("HTTP-Referer", referer.Trim());
            var title = ContextCoachConfig.Current.LlmAppTitle;
            if (!string.IsNullOrWhiteSpace(title))
                req.Headers.TryAddWithoutValidation("X-Title", title.Trim());
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            Log.Info($"[ContextCoach][LLM] deck-summary POST corr={corr} model={model} timeout={timeoutSec}s bytes={reqBytes}");

            string raw;
            try
            {
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Warn($"[ContextCoach][LLM] deck-summary HTTP {(int)resp.StatusCode} corr={corr} body_head={Truncate(raw, 400)}");
                    throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                }
            }
            catch (OperationCanceledException oce) when (cts.IsCancellationRequested)
            {
                Log.Warn($"[ContextCoach][LLM] deck-summary timed out corr={corr} ({oce.GetType().Name})");
                throw new InvalidOperationException($"LLM deck summary timed out after {timeoutSec}s.", oce);
            }

            var content = ExtractAssistantContent(raw);
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Empty assistant message");

            transcriptBasename = LlmTranscriptLogger.TryWrite(corr, model, system, user, raw, content);

            var profile = ParseDeckProfile(content, signature, state.Floor);
            RunLogger.LogLlmDeckSummary(
                state, corr, signature, model, reqBytes, "ok", transcriptBasename, null);
            return profile;
        }
        catch (Exception ex)
        {
            RunLogger.LogLlmDeckSummary(
                state,
                corr,
                signature,
                model,
                reqBytes,
                ClassifyDeckSummaryException(ex),
                transcriptBasename,
                ex.Message);
            throw;
        }
    }

    private static List<(string internalName, bool upgraded, int? coachScore)> BuildCoachTopTelemetry(
        IReadOnlyDictionary<string, LlmCardAdvice> parsed,
        IReadOnlyList<LlmCoachCandidate> candidates) =>
        candidates
            .Select(c => (c.InternalName, c.Upgraded, parsed.TryGetValue(c.AdviceMapKey, out var a) ? a.CoachScore : (int?)null))
            .Where(x => x.Item3.HasValue)
            .OrderByDescending(x => x.Item3 ?? -1)
            .Take(5)
            .Select(x => (x.InternalName, x.Upgraded, x.Item3))
            .ToList();

    private static string ClassifyCoachBatchException(Exception ex)
    {
        if (ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        if (ex.Message.StartsWith("HTTP ", StringComparison.Ordinal))
            return "http_error";
        if (ex.Message.Contains("Empty assistant", StringComparison.OrdinalIgnoreCase))
            return "empty_assistant";
        if (ex.Message.Contains("did not match any candidate", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Missing rankings", StringComparison.OrdinalIgnoreCase))
            return "parse_error";
        return "error";
    }

    private static string ClassifyDeckSummaryException(Exception ex)
    {
        if (ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        if (ex.Message.StartsWith("HTTP ", StringComparison.Ordinal))
            return "http_error";
        if (ex.Message.Contains("Empty assistant", StringComparison.OrdinalIgnoreCase))
            return "empty_assistant";
        if (ex.Message.Contains("Deck summary JSON was empty", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase))
            return "parse_error";
        return "error";
    }

    private static List<(string name, string note, int? score)> ToRankedList(
        IReadOnlyDictionary<string, LlmCardAdvice> map,
        IReadOnlyList<LlmCoachCandidate> candidates)
    {
        var rows = candidates
            .Select(c => (c.InternalName, map.TryGetValue(c.AdviceMapKey, out var a) ? a : null))
            .Where(x => x.Item2 != null)
            .Select(x => (x.InternalName, x.Item2!.CoachNote, x.Item2.CoachScore))
            .OrderByDescending(x => x.CoachScore ?? -1)
            .ToList();
        return rows;
    }

    public static LlmOverlayBatchStatus TryGetAdvice(string adviceMapKey, string batchKey, out LlmCardAdvice? advice, out string? error)
    {
        advice = null;
        error = null;
        lock (Gate)
        {
            if (_uiBatchKey == null || !string.Equals(_uiBatchKey, batchKey, StringComparison.Ordinal))
            {
                return LlmOverlayBatchStatus.Idle;
            }

            if (_status == LlmOverlayBatchStatus.Pending && _pendingSinceTicks != 0)
            {
                var limitMs = ContextCoachConfig.EffectiveLlmTimeoutSeconds * 1000L + DebounceMs + 15000L;
                var elapsed = Environment.TickCount64 - _pendingSinceTicks;
                if (elapsed > limitMs)
                {
                    Log.Warn($"[ContextCoach][LLM] watchdog: pending {elapsed}ms (limit {limitMs}ms) — forcing failed");
                    _requestSeq++;
                    _status = LlmOverlayBatchStatus.Failed;
                    _lastError =
                        "LLM did not finish in time (or requests kept restarting). Check the game log for repeated [ContextCoach][LLM] schedule lines.";
                    _pendingSinceTicks = 0;
                    _adviceByKey = new Dictionary<string, LlmCardAdvice>(StringComparer.OrdinalIgnoreCase);
                }
            }

            switch (_status)
            {
                case LlmOverlayBatchStatus.Pending:
                    return LlmOverlayBatchStatus.Pending;
                case LlmOverlayBatchStatus.Failed:
                    error = _lastError;
                    return LlmOverlayBatchStatus.Failed;
                case LlmOverlayBatchStatus.Ready:
                    _adviceByKey.TryGetValue(adviceMapKey, out advice);
                    return LlmOverlayBatchStatus.Ready;
                default:
                    return LlmOverlayBatchStatus.Idle;
            }
        }
    }

    /// <summary>Clears in-flight UI batch state when switching back to heuristic scoring or unloading.</summary>
    public static void ResetForHeuristicMode()
    {
        lock (Gate)
        {
            _scheduleVersion++;
            _requestSeq++;
            _uiBatchKey = null;
            _status = LlmOverlayBatchStatus.Idle;
            _lastError = null;
            _adviceByKey = new Dictionary<string, LlmCardAdvice>(StringComparer.OrdinalIgnoreCase);
            _lastCandidates = null;
            _lastGoodBatchKey = null;
            _lastGoodDeckSnapshot = null;
            _lastGoodCandidateNames = null;
            _lastGoodBatchSummary = null;
            _pendingSinceTicks = 0;
            _deckProfile = null;
            _deckProfilePendingSignature = null;
            _deckProfileSeq++;
        }

        Log.Info("[ContextCoach][LLM] reset batch state (heuristic mode or UI toggle)");
    }

    private const int MaxCardTextCharsForLlm = 360;

    private static string BuildSystemPrompt(string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        var languageDirective = IsChineseLanguage(lang)
            ? "Output language: Simplified Chinese (zh-CN). Write every coach_note in natural Simplified Chinese."
            : "Output language: English (en). Write every coach_note in concise natural English.";

        return """
You help a Slay the Spire 2 player choose cards during shop visits and card rewards.
You receive a JSON snapshot: run stats, deck summary, relics, on-screen card options, heuristic scores, and per-card metadata_hint.

""" + languageDirective + """

JSON field meanings (read carefully — common model mistakes):
- player_energy_per_turn: how much energy the player starts each turn with in combat (often 3). This is NOT any card's cost. Never describe a card as cheap or low-cost energy based on this number.
- energy_cost (per candidate): energy (E on the card) required to play that card when energy_cost_known is true. When energy_cost_known is false, the mod could not read the cost — do not assume 0, 1, or cheap; do not call the card free or low-cost.
- shop_price: gold paid at the merchant to add the card to the deck — not energy and not the same as energy_cost.

STS2 rules glossary (use these meanings; do not assume Slay the Spire 1-only mechanics unless the card text clearly matches):
- Energy: combat resource spent to play cards each turn (see player_energy_per_turn); distinct from gold (shop_price).
- Attack / Skill / Power: card types. Attacks mainly deal damage. Skills are non-Attack plays. Powers stay in play across turns until removed.
- Block: soaks incoming damage this turn (unless effects ignore or shred it).
- Exhaust (keyword on a card): after the card resolves, it is removed to the exhaust pile and normally does not return to your draw deck for later combats unless an effect retrieves it.
- Ethereal: if unplayed, discard at end of turn (typically).
- Retain: keep in hand into the next turn instead of discarding.
- Channel: add an orb (elemental passive) to your orb row; orbs trigger at turn start or when evoked depending on type.
- Focus: modifies how strong your orbs are (damage, block, etc. from orbs).
- Vulnerable / Weak / Frail: common debuffs (take more damage / deal less Attack damage / gain less Block from cards).
- Plating / Thorns / Dexterity / Strength: treat as in-game stat layers as printed on cards (exact stacking is engine-defined).

keyword_hints lists glossary entries only for keywords that appear in on-screen candidate card text OR in wiki metadata text for cards currently in the deck (same cap as deck_summary: top unique names by count).
Each metadata_hint may include wiki card_type, description text, card_type_tags, synergy_tags, role_tags, and upgrade notes — prefer description + tags over guessing effects.
Do not claim the deck has a specific strategy/synergy unless it is explicitly supported by deck_summary, relics, or coach_history.
If evidence is weak/absent, use uncertainty language instead of asserting a strategy.

coach_note style (mandatory):
- Do not answer by paraphrasing metadata_hint.description or restating generic card text — the player can read the card.
- Every coach_note must tie the pick to this run: cite at least one concrete hook from deck_summary, deck_profile.summary_lines (when present), relics, coach_history, or non-trivial hp/act/floor context when it changes the recommendation.
- When decision is "shop" and the top-level gold field is a number: for each candidate with numeric shop_price, if shop_price > gold then say clearly it is not affordable now and add one short clause on whether it is still a long-term priority or should be ignored; if shop_price <= gold, you may briefly mention tight gold vs other shop rows or removal when relevant.

For each card: assign coach_score 0–100 (higher = better pick now) and a very short coach_note (one line, about max ~120 UTF-8 characters — stay concise).
coach_note must be valid inside JSON: no double-quote characters, no line breaks, no trailing backslash — use plain text only.
Consider synergy with deck + relics, curve, win condition, and gold when shop_price is present.
shop_price is merchant gold only — never describe it as combat energy (E) or conflate it with energy_cost.
Each candidate’s shop_price is parsed from the UI — quote that number if you mention cost; do not guess a different gold value.
Respond with JSON ONLY (no markdown, no prose before/after), exactly this shape:
{"rankings":[{"internal_name":"string","upgraded":true|false,"coach_note":"string","coach_score":0}]}
Include one entry per candidate; internal_name + upgraded must match the input exactly. Close all brackets — output must be complete parseable JSON.
""";
    }

    private static (string json, int keywordHintCount) BuildUserPayload(
        string decisionType,
        GameState state,
        IReadOnlyList<LlmCoachCandidate> candidates)
    {
        var history = CoachPickHistory.SnapshotLinesForPrompt();
        var deckLines = SummarizeDeck(state.Deck);
        var relicLines = SummarizeRelics(state.Relics);

        var keywordScanBlobs = new List<string?>();
        foreach (var c in candidates)
            AppendCardMetadataTextsForKeywordScan(keywordScanBlobs, c.InternalName);
        foreach (var name in DeckUniqueNamesForKeywordScan(state.Deck))
            AppendCardMetadataTextsForKeywordScan(keywordScanBlobs, name);
        var keywordHints = KeywordGlossary.CollectHints(keywordScanBlobs, 32);

        var candPayload = candidates.Select(c => new
        {
            c.InternalName,
            c.Upgraded,
            energy_cost = c.EnergyCost,
            energy_cost_known = c.EnergyCost.HasValue,
            shop_price = c.ShopPrice,
            shop_discounted = c.ShopPrice != null ? c.ShopDiscounted : (bool?)null,
            augment_reason = c.AugmentReasonKey,
            heuristic = new
            {
                c.Heuristic.BaseScore,
                c.Heuristic.ContextScore,
                top_reasons = c.Heuristic.ReasonKeys.Zip(c.Heuristic.ReasonWeights,
                    (k, w) => new { key = k, weight = w }).Take(4).ToList()
            },
            metadata_hint = MetadataHint(c.InternalName, c.Upgraded)
        }).ToList();

        var payload = new
        {
            decision = decisionType,
            output_language = LocalizationManager.Language,
            screen_path = state.CurrentScreen,
            character = state.Character,
            act = state.Act,
            floor = state.Floor,
            ascension = state.Ascension,
            hp = state.Hp,
            max_hp = state.MaxHp,
            gold = state.Gold,
            player_energy_per_turn = state.MaxEnergy,
            deck_summary = deckLines,
            relics = relicLines,
            coach_history = history,
            deck_profile = GetDeckProfilePayload(state),
            keyword_hints = keywordHints.Count > 0
                ? keywordHints.Select(h => new { h.Term, h.Definition }).ToList()
                : null,
            candidates = candPayload
        };

        return (JsonSerializer.Serialize(payload, JsonOpt), keywordHints.Count);
    }

    private static bool IsChineseLanguage(string language)
    {
        return language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("zhs", StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetDeckProfilePayload(GameState state)
    {
        LlmDeckProfile? profile;
        lock (Gate)
            profile = _deckProfile;
        if (profile == null)
            return null;

        var lines = profile.ToSummaryLines(ContextCoachConfig.EffectiveLlmDeckProfileMaxLines);
        if (lines.Count == 0)
            return null;

        return new
        {
            version = profile.Version,
            generated_from_signature = profile.Signature,
            generated_floor = profile.GeneratedFloor,
            generated_utc = profile.GeneratedUtc,
            summary_lines = lines
        };
    }

    private static string BuildDeckSummarySystemPrompt(string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        var languageDirective = IsChineseLanguage(lang)
            ? "Write all text values in Simplified Chinese (zh-CN)."
            : "Write all text values in concise English (en).";
        return """
You summarize the current Slay the Spire 2 deck into compact strategic context for future card-pick prompts.
""" + languageDirective + """
Output JSON ONLY, exactly this shape:
{"core_plan":"string","enablers":["string"],"payoffs":["string"],"risks":["string"],"interactions":["string"]}
Rules:
- Keep each string short and concrete.
- interactions must contain 3-5 explicit card-name interactions when evidence exists.
- Use only evidence from provided deck/relic/card metadata; do not invent unseen cards.
- No markdown, no prose before/after JSON.
""";
    }

    private static string BuildDeckSummaryUserPayload(GameState state)
    {
        var deckLines = SummarizeDeck(state.Deck);
        var relicLines = SummarizeRelics(state.Relics);
        var keyCards = BuildDeckKeyCardHints(state.Deck, 12);
        var payload = new
        {
            output_language = LocalizationManager.Language,
            character = state.Character,
            act = state.Act,
            floor = state.Floor,
            ascension = state.Ascension,
            player_energy_per_turn = state.MaxEnergy,
            deck_summary = deckLines,
            relics = relicLines,
            deck_key_cards = keyCards
        };
        return JsonSerializer.Serialize(payload, JsonOpt);
    }

    private static List<object> BuildDeckKeyCardHints(IReadOnlyList<CardInstance>? deck, int maxUnique)
    {
        var result = new List<object>();
        if (deck == null || deck.Count == 0 || maxUnique <= 0)
            return result;

        var counts = new Dictionary<string, (int n, int up)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in deck)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                continue;
            if (!counts.TryGetValue(c.Name, out var t))
                t = (0, 0);
            t.n++;
            if (c.Upgraded) t.up++;
            counts[c.Name] = t;
        }

        foreach (var (name, stat) in counts
                     .OrderByDescending(kv => kv.Value.n)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Take(maxUnique))
        {
            if (!MetadataRepository.TryGetCard(name, out var meta) || meta == null)
                continue;
            result.Add(new
            {
                internal_name = name,
                display_name = meta.DisplayName,
                count = stat.n,
                upgraded_count = stat.up,
                description = TruncateForLlm(meta.Description, 180),
                card_type = meta.CardType,
                tags = meta.Tags.Take(10).ToList(),
                synergy_tags = meta.SynergyTags.Take(10).ToList()
            });
        }

        return result;
    }

    private static object? MetadataHint(string internalName, bool upgraded)
    {
        if (!MetadataRepository.TryGetCard(internalName, out var m) || m == null)
            return null;
        var desc = upgraded
            ? CoalesceNonEmpty(m.UpgradedDescription, m.Description)
            : m.Description;
        var descOther = upgraded ? m.Description : m.UpgradedDescription;
        return new
        {
            m.DisplayName,
            wiki_card_type = m.CardType,
            description = TruncateForLlm(desc, MaxCardTextCharsForLlm),
            description_other_upgrade_state = string.IsNullOrWhiteSpace(descOther) || string.Equals(
                NormalizeWs(descOther), NormalizeWs(desc ?? ""), StringComparison.Ordinal)
                ? null
                : TruncateForLlm(descOther, MaxCardTextCharsForLlm / 2),
            card_type_tags = m.Tags.Take(20).ToList(),
            synergy_tags = m.SynergyTags.Take(20).ToList(),
            role_tags = m.RoleTags.Take(12).ToList(),
            notes = TruncateForLlm(m.Notes, 220),
            m.UpgradeSummary,
            upgraded_focus = upgraded
        };
    }

    private static string? CoalesceNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : b;

    private static string NormalizeWs(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? TruncateForLlm(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (t.Length <= max) return t;
        return t[..max] + "…";
    }

    /// <summary>Un-truncated wiki strings used only to decide which keyword_hints to attach.</summary>
    private static void AppendCardMetadataTextsForKeywordScan(ICollection<string?> blobs, string internalName)
    {
        if (!MetadataRepository.TryGetCard(internalName, out var m) || m == null)
            return;
        AddKeywordScanBlob(blobs, m.Description);
        AddKeywordScanBlob(blobs, m.UpgradedDescription);
        AddKeywordScanBlob(blobs, m.Notes);
        AddKeywordScanBlob(blobs, m.UpgradeSummary);
        AddKeywordScanBlob(blobs, m.CardType);
        if (m.Tags.Count > 0)
            blobs.Add(string.Join(' ', m.Tags));
        if (m.SynergyTags.Count > 0)
            blobs.Add(string.Join(' ', m.SynergyTags));
        if (m.RoleTags.Count > 0)
            blobs.Add(string.Join(' ', m.RoleTags));
    }

    private static void AddKeywordScanBlob(ICollection<string?> blobs, string? s)
    {
        if (!string.IsNullOrWhiteSpace(s))
            blobs.Add(s);
    }

    /// <summary>Same ordering/cap as deck_summary (top 42 unique names by copy count).</summary>
    private static IEnumerable<string> DeckUniqueNamesForKeywordScan(IReadOnlyList<CardInstance>? deck)
    {
        if (deck == null || deck.Count == 0)
            yield break;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in deck)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            counts[c.Name] = counts.TryGetValue(c.Name, out var n) ? n + 1 : 1;
        }

        foreach (var name in counts.OrderByDescending(kv => kv.Value).Take(42).Select(kv => kv.Key))
            yield return name;
    }

    private static List<string> SummarizeDeck(IReadOnlyList<CardInstance>? deck)
    {
        var list = new List<string>();
        if (deck == null || deck.Count == 0)
        {
            list.Add("deck: (empty or unknown)");
            return list;
        }

        var counts = new Dictionary<string, (int n, int up)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in deck)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            if (!counts.TryGetValue(c.Name, out var t))
                t = (0, 0);
            t.n++;
            if (c.Upgraded) t.up++;
            counts[c.Name] = t;
        }

        foreach (var kv in counts.OrderByDescending(x => x.Value.n).Take(42))
        {
            var u = kv.Value.up > 0 ? $"+{kv.Value.up}↑" : "";
            list.Add($"{kv.Key} x{kv.Value.n}{u}");
        }

        if (counts.Count > 42)
            list.Add($"... ({counts.Count} unique types, truncated)");
        return list;
    }

    private static List<string> SummarizeRelics(IReadOnlyList<string>? relics)
    {
        if (relics == null || relics.Count == 0)
            return ["(none or unknown)"];
        var lines = new List<string>();
        foreach (var r in relics.Take(24))
        {
            if (MetadataRepository.TryGetRelic(r, out var m) && m != null && !string.IsNullOrWhiteSpace(m.Notes))
                lines.Add($"{r}: {m.Notes}");
            else
                lines.Add(r);
        }

        if (relics.Count > 24)
            lines.Add($"... +{relics.Count - 24} more");
        return lines;
    }

    private static Dictionary<string, int> CountDeck(IReadOnlyList<CardInstance>? deck)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (deck == null) return d;
        foreach (var c in deck)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            d[c.Name] = d.TryGetValue(c.Name, out var n) ? n + 1 : 1;
        }

        return d;
    }

    private static string ExtractAssistantContent(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return "";
            var msg = choices[0].GetProperty("message");
            if (!msg.TryGetProperty("content", out var contentEl))
                return "";
            if (contentEl.ValueKind == JsonValueKind.String)
                return contentEl.GetString() ?? "";
            if (contentEl.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in contentEl.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                        sb.Append(part.GetString());
                    else if (part.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                }

                return sb.ToString();
            }

            return contentEl.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] extract content: {ex.Message}");
            return "";
        }
    }

    internal static Dictionary<string, LlmCardAdvice> ParseRankings(string content, IReadOnlyList<LlmCoachCandidate> candidates)
    {
        var slice = StripMarkdownFence(content).Trim();
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        JsonException? lastJsonEx = null;
        for (var i = 0; i < slice.Length; i++)
        {
            if (slice[i] != '{') continue;
            var chunk = TrySliceBalancedJsonObject(slice, i);
            if (chunk == null) continue;
            try
            {
                using var doc = JsonDocument.Parse(chunk, options);
                var root = doc.RootElement;
                if (!root.TryGetProperty("rankings", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;
                var map = BuildAdviceMap(arr, candidates);
                if (map.Count > 0)
                    return map;
            }
            catch (JsonException ex)
            {
                lastJsonEx = ex;
            }
        }

        try
        {
            var tail = slice.TrimStart();
            var chunk = tail.Length > 0 && tail[0] == '{'
                ? TrySliceBalancedJsonObject(tail, 0) ?? tail
                : tail;
            using var doc = JsonDocument.Parse(chunk, options);
            if (!doc.RootElement.TryGetProperty("rankings", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Missing rankings[]");
            var finalMap = BuildAdviceMap(arr, candidates);
            if (finalMap.Count == 0)
                throw new InvalidOperationException("LLM JSON did not match any candidate");
            return finalMap;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Coach reply was not valid JSON with {\"rankings\":[...]}. Try a model that follows JSON instructions, or set llm_json_object_response_format=true in contextcoach.config.",
                lastJsonEx ?? ex);
        }
    }

    private static LlmDeckProfile ParseDeckProfile(string content, string signature, int? floor)
    {
        var slice = StripMarkdownFence(content).Trim();
        using var doc = JsonDocument.Parse(slice, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = doc.RootElement;

        var corePlan = root.TryGetProperty("core_plan", out var cp) && cp.ValueKind == JsonValueKind.String
            ? cp.GetString() ?? ""
            : "";
        var enablers = ReadStringArray(root, "enablers", 5);
        var payoffs = ReadStringArray(root, "payoffs", 5);
        var risks = ReadStringArray(root, "risks", 4);
        var interactions = ReadStringArray(root, "interactions", 6);

        if (string.IsNullOrWhiteSpace(corePlan) && enablers.Count == 0 && payoffs.Count == 0 && risks.Count == 0 && interactions.Count == 0)
            throw new InvalidOperationException("Deck summary JSON was empty.");

        return new LlmDeckProfile
        {
            Signature = signature,
            GeneratedUtc = DateTimeOffset.UtcNow,
            GeneratedFloor = floor,
            Version = 1,
            CorePlan = Truncate(corePlan, 200),
            Enablers = enablers,
            Payoffs = payoffs,
            Risks = risks,
            Interactions = interactions
        };
    }

    private static List<string> ReadStringArray(JsonElement root, string prop, int maxItems)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var text = (item.GetString() ?? "").Trim();
            if (text.Length == 0)
                continue;
            list.Add(Truncate(text, 180));
            if (list.Count >= maxItems)
                break;
        }

        return list;
    }

    /// <summary>Parses a single top-level object when the model prefixes prose or suffixes text after the JSON.</summary>
    private static string? TrySliceBalancedJsonObject(string s, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= s.Length || s[openBraceIndex] != '{')
            return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var j = openBraceIndex; j < s.Length; j++)
        {
            var c = s[j];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return s.Substring(openBraceIndex, j - openBraceIndex + 1);
            }
        }

        return null;
    }

    private static Dictionary<string, LlmCardAdvice> BuildAdviceMap(JsonElement rankingsArray, IReadOnlyList<LlmCoachCandidate> candidates)
    {
        var map = new Dictionary<string, LlmCardAdvice>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in rankingsArray.EnumerateArray())
        {
            var name = el.TryGetProperty("internal_name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var upgraded = el.TryGetProperty("upgraded", out var u) && u.ValueKind == JsonValueKind.True;
            var note = el.TryGetProperty("coach_note", out var cn) ? cn.GetString() ?? "" : "";
            int? score = null;
            if (el.TryGetProperty("coach_score", out var cs))
            {
                if (cs.ValueKind == JsonValueKind.Number && cs.TryGetInt32(out var iv))
                    score = iv;
            }

            var key = $"{name}|u{(upgraded ? 1 : 0)}";
            if (!candidates.Any(c => string.Equals(c.InternalName, name, StringComparison.Ordinal) && c.Upgraded == upgraded))
                continue;
            map[key] = new LlmCardAdvice { CoachNote = note.Trim(), CoachScore = score };
        }

        return map;
    }

    private static string StripMarkdownFence(string content)
    {
        var s = content.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0)
                s = s[(nl + 1)..];
            var end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 0)
                s = s[..end];
        }

        return s.Trim();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..max] + "...";
    }

    private sealed class ChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public List<ChatMessage> Messages { get; set; } = [];
        public double Temperature { get; set; }
        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        /// <summary>OpenAI-style hint; skipped when null (some models reject it).</summary>
        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LlmJsonResponseFormat? ResponseFormat { get; set; }
    }

    private sealed class LlmJsonResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "json_object";
    }

    private sealed class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
