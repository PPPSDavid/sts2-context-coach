using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Logging;
using Sts2ContextCoach.Diagnostics;
using Sts2ContextCoach.Scoring;
using Sts2ContextCoach.State;

namespace Sts2ContextCoach.Telemetry;

internal static class RunLogger
{
    private const string DataVersion = "2";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string? _runId;
    private static string? _runDir;
    private static string? _eventsPath;
    private static DateTimeOffset _runStartedAt;
    private static bool _runFinishedLogged;
    private static string _runOutcome = "active";
    private static int _eventCount;
    private static int _decisionCount;
    private static int _combatCount;
    private static int? _lastHp;
    private static int? _lastFloor;
    private static string? _lastEncounter;
    private static int _decisionSeq;
    private static int _combatStartHp;
    private static int _combatTurns;
    private static bool _combatActive;
    private static int _combatEnterStreak;
    private static int _combatExitStreak;
    private static long _lastCombatTurnSampleMs;
    private static long _lastAggregateResyncMs;
    private static long _lastWriteSummaryMs;
    /// <summary>Many card overlays call ObserveRunState on independent timers; coalesce to one pass per interval.</summary>
    private static long _lastObserveRunStateMs;
    private static string? _metadataWrittenCharacter;
    private static readonly HashSet<string> SeenDecisionFingerprints = new(StringComparer.Ordinal);
    private static readonly List<PendingDecision> PendingRewardDecisions = [];
    private static readonly Dictionary<string, DecisionAttributionState> DecisionAttribution = new(StringComparer.Ordinal);
    private static Dictionary<string, int>? _lastDeckCounts;

    public static bool IsEnabled => ContextCoachConfig.Current.LoggingEnabled;
    public static string? CurrentRunId => _runId;
    public static int LastExportedRunCount { get; private set; }

    public static void EnsureInitialized(GameState state)
    {
        if (!IsEnabled)
            return;

        lock (Gate)
        {
            try
            {
                if (_runId != null && _runDir != null && Directory.Exists(_runDir))
                    return;
                if (TryResumeActiveRunLocked(state))
                    return;
                InitializeRunLocked(state);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach] RunLogger init failed: {ex.Message}");
            }
        }
    }

    public static void LogDecision(
        string decisionType,
        GameState state,
        IReadOnlyList<string> candidates,
        IReadOnlyDictionary<string, ScoreResult> scores,
        string? playerChoice,
        string? source = null)
    {
        if (!IsEnabled)
            return;

        EnsureInitialized(state);
        if (_runId == null)
            return;

        lock (Gate)
        {
            try
            {
                var fingerprint = BuildDecisionFingerprint(decisionType, candidates, state);
                if (!SeenDecisionFingerprints.Add(fingerprint))
                    return;

                var scoreMap = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kv in scores)
                {
                    var breakdown = kv.Value.Breakdown
                        .Select(b => new { reason = b.Key, weight = MathF.Round(b.Weight, 3) })
                        .ToArray();

                    scoreMap[kv.Key] = new
                    {
                        base_score = MathF.Round(kv.Value.BaseScore, 3),
                        context_score = MathF.Round(kv.Value.ContextScore, 3),
                        score_breakdown = breakdown,
                        reasons = kv.Value.ReasonKeys
                    };
                }

                _decisionSeq++;
                _decisionCount++;
                var decisionId = $"{_runId}-d{_decisionSeq:D4}";
                var ranked = scoreMap
                    .Select(kv =>
                    {
                        var raw = kv.Value?.GetType().GetProperty("context_score")?.GetValue(kv.Value);
                        var sc = raw is float f ? f : raw is double d ? (float)d : raw is decimal m ? (float)m : 0f;
                        return (name: kv.Key, score: sc);
                    })
                    .OrderByDescending(x => x.score)
                    .ToList();
                var recommended = ranked.FirstOrDefault().name;
                var second = ranked.Skip(1).FirstOrDefault();
                var scoreGap = ranked.Count > 1 ? ranked[0].score - second.score : (float?)null;
                var confidence = scoreGap is null
                    ? 0.5f
                    : Math.Clamp(scoreGap.Value / 8f, 0.05f, 0.95f);
                EmitEvent(new
                {
                    event_type = "decision",
                    run_id = _runId,
                    decision_id = decisionId,
                    decision_type = decisionType,
                    timestamp = DateTimeOffset.UtcNow,
                    source,
                    game_state = ToSnapshot(state),
                    candidate_options = candidates,
                    engine_scores = scoreMap,
                    recommended_choice = recommended,
                    player_choice = playerChoice,
                    accepted_recommendation = !string.IsNullOrWhiteSpace(playerChoice) && string.Equals(playerChoice, recommended, StringComparison.Ordinal),
                    top1_top2_score_gap = scoreGap,
                    engine_confidence = MathF.Round(confidence, 3)
                });

                DecisionAttribution[decisionId] = new DecisionAttributionState
                {
                    DecisionId = decisionId,
                    DecisionType = decisionType,
                    CreatedAt = DateTimeOffset.UtcNow,
                    StartFloor = state.Floor,
                    StartHp = state.Hp,
                    StartGold = state.Gold,
                    StartDeckSize = state.Deck?.Count
                };

                if (string.Equals(decisionType, "card_reward", StringComparison.OrdinalIgnoreCase))
                {
                    PendingRewardDecisions.Add(new PendingDecision
                    {
                        DecisionId = decisionId,
                        Candidates = candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList(),
                        Floor = state.Floor,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach] RunLogger decision failed: {ex.Message}");
            }
        }
    }

    /// <summary>Correlates in-game LLM coach HTTP calls with <c>godot.log</c> and optional transcript files.</summary>
    public static void LogLlmCoachBatch(
        GameState state,
        string corr,
        string decisionType,
        string batchKey,
        string model,
        int requestBodyBytes,
        string outcome,
        string? transcriptBasename,
        string? errorDetail,
        IReadOnlyList<(string internalName, bool upgraded, int? coachScore)>? llmTop)
    {
        if (!IsEnabled)
            return;
        EnsureInitialized(state);
        if (_runId == null)
            return;

        lock (Gate)
        {
            try
            {
                EmitEvent(new
                {
                    event_type = "llm_coach_batch",
                    run_id = _runId,
                    timestamp = DateTimeOffset.UtcNow,
                    corr,
                    decision_type = decisionType,
                    batch_key = batchKey,
                    model,
                    request_body_bytes = requestBodyBytes,
                    outcome,
                    transcript_file = transcriptBasename,
                    error = TruncateTelemetryString(errorDetail, 480),
                    llm_top = llmTop is { Count: > 0 }
                        ? llmTop.Select(x => new
                        {
                            internal_name = x.internalName,
                            upgraded = x.upgraded,
                            coach_score = x.coachScore
                        }).ToList()
                        : null,
                    game_state = ToSnapshot(state)
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach] RunLogger llm_coach_batch failed: {ex.Message}");
            }
        }
    }

    /// <summary>Deck profile / strategic summary LLM call (separate HTTP path from row coach).</summary>
    public static void LogLlmDeckSummary(
        GameState state,
        string corr,
        string deckSignature,
        string model,
        int requestBodyBytes,
        string outcome,
        string? transcriptBasename,
        string? errorDetail)
    {
        if (!IsEnabled)
            return;
        EnsureInitialized(state);
        if (_runId == null)
            return;

        lock (Gate)
        {
            try
            {
                EmitEvent(new
                {
                    event_type = "llm_deck_summary",
                    run_id = _runId,
                    timestamp = DateTimeOffset.UtcNow,
                    corr,
                    deck_signature = deckSignature,
                    model,
                    request_body_bytes = requestBodyBytes,
                    outcome,
                    transcript_file = transcriptBasename,
                    error = TruncateTelemetryString(errorDetail, 480),
                    game_state = ToSnapshot(state)
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach] RunLogger llm_deck_summary failed: {ex.Message}");
            }
        }
    }

    /// <summary>Copies a global transcript into <c>{run}/llm/</c> when <see cref="ContextCoachConfig.LlmMirrorTranscriptsIntoRunFolder"/> is enabled.</summary>
    public static void TryMirrorLlmTranscript(string transcriptAbsolutePath, string basename)
    {
        if (!IsEnabled || !ContextCoachConfig.Current.LlmMirrorTranscriptsIntoRunFolder)
            return;
        if (!File.Exists(transcriptAbsolutePath))
            return;

        try
        {
            lock (Gate)
            {
                if (_runDir == null)
                    return;
                var destDir = Path.Combine(_runDir, "llm");
                Directory.CreateDirectory(destDir);
                File.Copy(transcriptAbsolutePath, Path.Combine(destDir, basename), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] LLM transcript mirror skipped: {ex.Message}");
        }
    }

    private static string? TruncateTelemetryString(string? s, int maxLen)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        return s.Length <= maxLen ? s : s[..maxLen];
    }

    public static void ObserveRunState(GameState state)
    {
        if (!IsEnabled)
            return;
        EnsureInitialized(state);
        if (_runId == null)
            return;

        var nowObserve = Environment.TickCount64;
        if (_lastObserveRunStateMs != 0 && nowObserve - _lastObserveRunStateMs < 100)
            return;
        _lastObserveRunStateMs = nowObserve;

        lock (Gate)
        {
            try
            {
                if (_runFinishedLogged)
                    return;

                DetectAndRotateRun(state);
                TryAutoCloseTerminalRun(state);
                MaybeRefreshMetadata(state);
                ContextCoachLogging.DumpGameState(state, "RunLogger.ObserveRunState");
                ResolvePendingRewardChoices(state);
                UpdateDecisionAttribution(state);
                LogCombatIfAny(state);
                LogPathIfAny(state);
                _lastHp = state.Hp;
                _lastFloor = state.Floor;
                if (_runDir != null && !_runFinishedLogged)
                {
                    var nowSummary = Environment.TickCount64;
                    if (nowSummary - _lastWriteSummaryMs >= 3500)
                    {
                        _lastWriteSummaryMs = nowSummary;
                        WriteSummary(state, "active");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach] RunLogger observe failed: {ex.Message}");
            }
        }
    }

    private static void DetectAndRotateRun(GameState state)
    {
        if (_runId == null || _runFinishedLogged)
            return;

        // Avoid false positives when save/load or MP snapshots briefly report floor 1 starter stats.
        var looksLikeFreshRun =
            state.Floor is <= 1 &&
            state.Act is <= 1 &&
            state.Deck?.Count is <= 10 &&
            state.MaxEnergy is <= 3 &&
            _lastFloor is >= 4;
        if (!looksLikeFreshRun)
            return;

        EmitEvent(new
        {
            event_type = "run_finished",
            run_id = _runId,
            timestamp = DateTimeOffset.UtcNow,
            status = "user_gave_up",
            state = ToSnapshot(state)
        });
        _runFinishedLogged = true;
        _runOutcome = "user_gave_up";
        WriteSummary(state, "user_gave_up");
        ClearActiveRunPointer();

        _runId = null;
        _runDir = null;
        _eventsPath = null;
        _runFinishedLogged = false;
        _runOutcome = "active";
        _eventCount = 0;
        _decisionCount = 0;
        _combatCount = 0;
        _decisionSeq = 0;
        _combatActive = false;
        _combatEnterStreak = 0;
        _combatExitStreak = 0;
        _combatTurns = 0;
        _combatStartHp = 0;
        _lastEncounter = null;
        _lastDeckCounts = null;
        PendingRewardDecisions.Clear();
        DecisionAttribution.Clear();

        InitializeRunLocked(state);
    }

    private static string ActiveRunPointerPath() =>
        Path.Combine(ContextCoachConfig.GetLogsRootPath(), "active_run.json");

    private static void WriteActiveRunPointer()
    {
        if (_runId == null) return;
        try
        {
            var logsRoot = ContextCoachConfig.GetLogsRootPath();
            Directory.CreateDirectory(logsRoot);
            File.WriteAllText(ActiveRunPointerPath(), JsonSerializer.Serialize(new { run_id = _runId }, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] WriteActiveRunPointer failed: {ex.Message}");
        }
    }

    private static void ClearActiveRunPointer()
    {
        try
        {
            var path = ActiveRunPointerPath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] ClearActiveRunPointer failed: {ex.Message}");
        }
    }

    private static bool TryResumeActiveRunLocked(GameState state)
    {
        var path = ActiveRunPointerPath();
        if (!File.Exists(path))
            return false;

        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var rid = doc?["run_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rid))
            {
                ClearActiveRunPointer();
                return false;
            }

            var runDir = Path.Combine(ContextCoachConfig.GetLogsRootPath(), rid);
            var eventsPath = Path.Combine(runDir, "events.jsonl");
            if (!Directory.Exists(runDir) || !File.Exists(eventsPath))
            {
                ClearActiveRunPointer();
                return false;
            }

            if (IsRunFolderTerminal(runDir))
            {
                ClearActiveRunPointer();
                return false;
            }

            _runId = rid;
            _runDir = runDir;
            _eventsPath = eventsPath;
            _runFinishedLogged = false;
            _runOutcome = "active";
            _lastHp = null;
            _lastFloor = null;
            _lastEncounter = null;
            _combatActive = false;
            _combatEnterStreak = 0;
            _combatExitStreak = 0;
            _combatTurns = 0;
            _combatStartHp = 0;
            _lastCombatTurnSampleMs = 0;
            _lastDeckCounts = null;
            _metadataWrittenCharacter = null;

            _runStartedAt = ReadRunStartedAtFromLog(eventsPath) ?? DateTimeOffset.UtcNow;

            RehydrateFromEventsFile();
            if (_runFinishedLogged)
            {
                ClearActiveRunPointer();
                _runId = null;
                _runDir = null;
                _eventsPath = null;
                return false;
            }

            if (LooksLikeFreshRunAfterAbandonedSession(eventsPath, state))
            {
                ClearActiveRunPointer();
                _runId = null;
                _runDir = null;
                _eventsPath = null;
                return false;
            }

            WriteActiveRunPointer();
            MaybeRefreshMetadata(state);
            if (ContextCoachLogging.Verbose)
                Log.Info($"[ContextCoach] Resumed run logging for {_runId} (events={_eventCount})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] Resume run failed: {ex.Message}");
            ClearActiveRunPointer();
            return false;
        }
    }

    private static bool IsRunFolderTerminal(string runDir)
    {
        try
        {
            var summaryPath = Path.Combine(runDir, "summary.json");
            if (File.Exists(summaryPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(summaryPath)) as JsonObject;
                var st = node?["status"]?.GetValue<string>() ?? node?["run_outcome"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(st) &&
                    !st.Equals("active", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var eventsPath = Path.Combine(runDir, "events.jsonl");
            if (File.Exists(eventsPath))
            {
                foreach (var line in File.ReadLines(eventsPath))
                {
                    if (line.Contains("\"event_type\":\"run_finished\"", StringComparison.Ordinal))
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>
    /// If the log shows a deep run but the current snapshot is starter stats, assume the player started a new run
    /// and do not append to the old log.
    /// </summary>
    private static bool LooksLikeFreshRunAfterAbandonedSession(string eventsPath, GameState state)
    {
        try
        {
            var lastProgFloor = ReadLastProgressFloorFromLog(eventsPath);
            if (lastProgFloor is null || lastProgFloor < 5)
                return false;
            if (state.Floor is int cf && cf <= 1 &&
                state.Act is int ca && ca <= 1 &&
                state.Deck?.Count is int ds && ds <= 12)
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static int? ReadLastProgressFloorFromLog(string eventsPath)
    {
        int? best = null;
        foreach (var line in File.ReadLines(eventsPath))
        {
            if (!line.Contains("\"floor\"", StringComparison.Ordinal))
                continue;
            try
            {
                var node = JsonNode.Parse(line) as JsonObject;
                var gs = node?["state"] as JsonObject
                           ?? node?["game_state"] as JsonObject;
                if (gs?["floor"] is JsonValue fv && fv.TryGetValue(out int fl))
                    best = best is int b ? Math.Max(b, fl) : fl;
            }
            catch
            {
                // skip line
            }
        }

        return best;
    }

    private static DateTimeOffset? ReadRunStartedAtFromLog(string eventsPath)
    {
        try
        {
            foreach (var line in File.ReadLines(eventsPath))
            {
                if (!line.Contains("\"event_type\":\"run_started\"", StringComparison.Ordinal))
                    continue;
                var node = JsonNode.Parse(line) as JsonObject;
                var ts = node?["timestamp"];
                if (ts is null) return null;
                return ts.GetValue<DateTimeOffset>();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static readonly Regex DecisionIdSeq = new(@"-d(\d{4})""", RegexOptions.Compiled);

    private static void RehydrateFromEventsFile()
    {
        SeenDecisionFingerprints.Clear();
        PendingRewardDecisions.Clear();
        DecisionAttribution.Clear();
        _eventCount = 0;
        _decisionCount = 0;
        _combatCount = 0;
        _decisionSeq = 0;
        _runFinishedLogged = false;

        if (string.IsNullOrEmpty(_eventsPath) || !File.Exists(_eventsPath))
            return;

        foreach (var line in File.ReadLines(_eventsPath))
        {
            _eventCount++;
            if (line.Contains("\"event_type\":\"combat\"", StringComparison.Ordinal))
                _combatCount++;
            if (line.Contains("\"event_type\":\"decision\"", StringComparison.Ordinal))
            {
                _decisionCount++;
                TryRehydrateFingerprint(line);
                TryRehydrateDecisionSeq(line);
            }

            if (line.Contains("\"event_type\":\"run_finished\"", StringComparison.Ordinal))
                _runFinishedLogged = true;
        }
    }

    private static void TryRehydrateDecisionSeq(string line)
    {
        var m = DecisionIdSeq.Match(line);
        if (!m.Success) return;
        if (int.TryParse(m.Groups[1].Value, out var n))
            _decisionSeq = Math.Max(_decisionSeq, n);
    }

    private static void TryRehydrateFingerprint(string line)
    {
        try
        {
            var node = JsonNode.Parse(line) as JsonObject;
            if (node is null) return;
            var dtype = node["decision_type"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(dtype)) return;
            var candidates = node["candidate_options"] as JsonArray;
            var list = new List<string>();
            if (candidates != null)
            {
                foreach (var c in candidates)
                {
                    var s = c?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
            }

            var gs = node["game_state"] as JsonObject;
            var floorStr = gs?["floor"]?.ToString() ?? "?";
            var screenStr = gs?["current_screen"]?.GetValue<string>() ?? "?";
            var fp = $"{dtype}|{floorStr}|{screenStr}|{string.Join(",", list.OrderBy(x => x, StringComparer.Ordinal))}";
            SeenDecisionFingerprints.Add(fp);
        }
        catch
        {
            // ignore bad lines
        }
    }

    private static void MaybeRefreshMetadata(GameState state)
    {
        if (_runDir == null || string.IsNullOrWhiteSpace(state.Character))
            return;
        var c = state.Character.Trim();
        if (c.Length < 2) return;
        if (string.Equals(_metadataWrittenCharacter, c, StringComparison.OrdinalIgnoreCase))
            return;

        WriteMetadata(state);
        _metadataWrittenCharacter = c;
    }

    private static void InitializeRunLocked(GameState state)
    {
        _runId = BuildRunId();
        _combatActive = false;
        _combatEnterStreak = 0;
        _combatExitStreak = 0;
        _runStartedAt = DateTimeOffset.UtcNow;
        _runOutcome = "active";
        _runDir = Path.Combine(ContextCoachConfig.GetLogsRootPath(), _runId);
        _eventsPath = Path.Combine(_runDir, "events.jsonl");
        Directory.CreateDirectory(_runDir);
        _metadataWrittenCharacter = state.Character;
        WriteMetadata(state);
        WriteSummary(state, "active");
        EmitEvent(new
        {
            event_type = "run_started",
            run_id = _runId,
            timestamp = DateTimeOffset.UtcNow,
            state = ToSnapshot(state)
        });
        WriteActiveRunPointer();
    }

    public static string? ExportLogs(string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return ExportUnpublishedLogs();

        try
        {
            var id = runId;
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var runDir = Path.Combine(ContextCoachConfig.GetLogsRootPath(), id);
            if (!Directory.Exists(runDir))
                return null;

            var exportsDir = Path.Combine(ContextCoachConfig.GetExportsRootPath());
            Directory.CreateDirectory(exportsDir);
            var zipPath = Path.Combine(exportsDir, $"sts2-context-coach-{id}.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddIfExists(archive, Path.Combine(runDir, "summary.json"), "summary.json");
                AddIfExists(archive, Path.Combine(runDir, "metadata.json"), "metadata.json");
                AddIfExists(archive, Path.Combine(runDir, "events.jsonl"), "events.jsonl");
                AddLlmTranscriptEntries(archive, runDir, "");
            }
            MarkRunExported(id, zipPath);
            LastExportedRunCount = 1;

            if (ContextCoachLogging.Verbose)
                Log.Info($"[ContextCoach] Exported run logs to {zipPath}");
            return zipPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] ExportLogs failed: {ex.Message}");
            return null;
        }
    }

    public static string? ExportUnpublishedLogs()
    {
        try
        {
            var logsRoot = ContextCoachConfig.GetLogsRootPath();
            if (!Directory.Exists(logsRoot))
                return null;

            var state = ReadExportState();
            var runDirs = Directory.GetDirectories(logsRoot)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            var unpublished = runDirs
                .Where(dir => !state.ExportedRunIds.Contains(Path.GetFileName(dir)))
                .ToList();

            if (unpublished.Count == 0)
                return null;

            var exportsDir = Path.Combine(ContextCoachConfig.GetExportsRootPath());
            Directory.CreateDirectory(exportsDir);
            var bundleId = $"bundle-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var zipPath = Path.Combine(exportsDir, $"sts2-context-coach-{bundleId}.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var dir in unpublished)
                {
                    var runId = Path.GetFileName(dir);
                    AddIfExists(archive, Path.Combine(dir, "summary.json"), $"{runId}/summary.json");
                    AddIfExists(archive, Path.Combine(dir, "metadata.json"), $"{runId}/metadata.json");
                    AddIfExists(archive, Path.Combine(dir, "events.jsonl"), $"{runId}/events.jsonl");
                    AddLlmTranscriptEntries(archive, dir, $"{runId}/");
                    state.ExportedRunIds.Add(runId);
                }
            }

            state.Bundles.Add(new ExportBundle
            {
                BundlePath = zipPath,
                ExportedAt = DateTimeOffset.UtcNow,
                RunIds = unpublished
                    .Select(Path.GetFileName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToList()
            });
            WriteExportState(state);
            LastExportedRunCount = unpublished.Count;
            return zipPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] ExportUnpublishedLogs failed: {ex.Message}");
            return null;
        }
    }

    public static void TryFinishRun(GameState state, string status = "ended")
    {
        if (!IsEnabled)
            return;
        EnsureInitialized(state);
        if (_runId == null)
            return;

        lock (Gate)
        {
            if (_runFinishedLogged)
                return;

            try
            {
                UpdateDecisionAttribution(state, forceSnapshot: true);
                EmitEvent(new
                {
                    event_type = "run_finished",
                    run_id = _runId,
                    timestamp = DateTimeOffset.UtcNow,
                    status,
                    state = ToSnapshot(state)
                });
                _runFinishedLogged = true;
                _runOutcome = status;
                WriteSummary(state, status);
                ClearActiveRunPointer();
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach] Run finish log failed: {ex.Message}");
            }
        }
    }

    private const int CombatScreenStreakRequired = 4;

    private static void LogCombatIfAny(GameState state)
    {
        var inCombat = IsCombatScreen(state.CurrentScreen);
        if (inCombat)
        {
            _combatExitStreak = 0;
            if (!_combatActive)
            {
                _combatEnterStreak++;
                if (_combatEnterStreak < CombatScreenStreakRequired)
                    return;

                _combatEnterStreak = 0;
                _combatActive = true;
                _combatStartHp = state.Hp ?? -1;
                _combatTurns = 0;
                _lastCombatTurnSampleMs = 0;
                _lastEncounter = GuessEncounterName(state.CurrentScreen);
                return;
            }

            _combatEnterStreak = 0;
            var now = Environment.TickCount64;
            if (_lastCombatTurnSampleMs == 0 || now - _lastCombatTurnSampleMs >= 1200)
            {
                _lastCombatTurnSampleMs = now;
                _combatTurns++;
            }

            return;
        }

        _combatEnterStreak = 0;
        if (!_combatActive)
        {
            _combatExitStreak = 0;
            return;
        }

        _combatExitStreak++;
        if (_combatExitStreak < CombatScreenStreakRequired)
            return;

        _combatExitStreak = 0;
        var endHp = state.Hp ?? _combatStartHp;
        var delta = endHp - _combatStartHp;
        _combatCount++;
        EmitEvent(new
        {
            event_type = "combat",
            run_id = _runId,
            timestamp = DateTimeOffset.UtcNow,
            encounter_name = _lastEncounter ?? "unknown",
            start_hp = _combatStartHp >= 0 ? (int?)_combatStartHp : null,
            end_hp = endHp,
            hp_delta = delta,
            turns = _combatTurns <= 0 ? (int?)null : _combatTurns,
            result = endHp > 0 ? "survived" : "unknown"
        });
        _combatActive = false;
        _combatStartHp = 0;
        _combatTurns = 0;
        _lastEncounter = null;
    }

    private static void LogPathIfAny(GameState state)
    {
        if (_lastFloor is null || state.Floor is null || state.Floor <= _lastFloor)
            return;

        _decisionCount++;
        EmitEvent(new
        {
            event_type = "decision",
            run_id = _runId,
            decision_id = $"{_runId}-d{++_decisionSeq:D4}",
            decision_type = "path",
            timestamp = DateTimeOffset.UtcNow,
            game_state = ToSnapshot(state),
            candidate_options = Array.Empty<string>(),
            engine_scores = new Dictionary<string, object>(),
            score_breakdown = Array.Empty<object>(),
            reasons = new[] { "path_advance_observed" },
            player_choice = $"floor_{state.Floor}"
        });
    }

    private static void ResolvePendingRewardChoices(GameState state)
    {
        var currentDeck = BuildDeckMultiset(state);
        if (_lastDeckCounts == null)
        {
            _lastDeckCounts = currentDeck;
            return;
        }

        var addedCard = FindSingleAddedCard(_lastDeckCounts, currentDeck);
        _lastDeckCounts = currentDeck;

        if (string.IsNullOrWhiteSpace(addedCard) || PendingRewardDecisions.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        for (var i = PendingRewardDecisions.Count - 1; i >= 0; i--)
        {
            var p = PendingRewardDecisions[i];
            if ((now - p.CreatedAt).TotalMinutes > 5)
            {
                PendingRewardDecisions.RemoveAt(i);
                continue;
            }

            if (p.Candidates.Contains(addedCard, StringComparer.Ordinal))
            {
                EmitEvent(new
                {
                    event_type = "decision_choice",
                    run_id = _runId,
                    decision_id = p.DecisionId,
                    timestamp = now,
                    player_choice = addedCard,
                    resolution = "deck_diff_inferred"
                });
                PendingRewardDecisions.RemoveAt(i);
                break;
            }
        }
    }

    private static object ToSnapshot(GameState state) => new
    {
        character = state.Character,
        hp = state.Hp,
        max_hp = state.MaxHp,
        gold = state.Gold,
        deck_size = state.Deck?.Count,
        relics_count = state.Relics?.Count,
        act = state.Act,
        floor = state.Floor,
        ascension = state.Ascension,
        max_energy = state.MaxEnergy,
        current_screen = state.CurrentScreen
    };

    private static string BuildRunId() =>
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

    private static void WriteMetadata(GameState state)
    {
        if (_runDir == null || _runId == null)
            return;

        var asm = Assembly.GetExecutingAssembly();
        var modVersion = asm.GetName().Version?.ToString() ?? "unknown";
        var engineVersion = ReadEngineVersion() ?? "unknown";
        var seed = TryReadSeed() ?? "unknown";
        var metadata = new
        {
            run_id = _runId,
            timestamp = _runStartedAt,
            character = state.Character,
            ascension = state.Ascension,
            seed,
            mod_version = modVersion,
            engine_version = engineVersion,
            data_version = DataVersion,
            privacy = new
            {
                collects_only_gameplay_data = true,
                collects_identity = false,
                collects_file_paths = false
            }
        };
        File.WriteAllText(Path.Combine(_runDir, "metadata.json"), JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static void WriteSummary(GameState state, string status)
    {
        if (_runDir == null || _runId == null)
            return;

        // Full-file rescan is expensive; only sync from disk when the run ends (or similar), not every ~3.5s "active" write.
        if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            RefreshAggregateCountsFromDiskIfDue();

        var summary = new
        {
            run_id = _runId,
            status,
            run_outcome = _runOutcome,
            started_at = _runStartedAt,
            updated_at = DateTimeOffset.UtcNow,
            event_count = _eventCount,
            decision_count = _decisionCount,
            combat_count = _combatCount,
            final_state = ToSnapshot(state)
        };
        File.WriteAllText(Path.Combine(_runDir, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions));
    }

    /// <summary>
    /// Keeps summary.json aligned with events.jsonl if in-memory counters drift (multi-card timers, resume, etc.).
    /// </summary>
    private static void RefreshAggregateCountsFromDiskIfDue()
    {
        if (string.IsNullOrEmpty(_eventsPath) || !File.Exists(_eventsPath))
            return;

        var now = Environment.TickCount64;
        if (now - _lastAggregateResyncMs < 60_000 && _eventCount > 0)
            return;
        _lastAggregateResyncMs = now;

        try
        {
            var events = 0;
            var decisions = 0;
            var combats = 0;
            foreach (var line in File.ReadLines(_eventsPath))
            {
                events++;
                if (line.Contains("\"event_type\":\"decision\"", StringComparison.Ordinal))
                    decisions++;
                if (line.Contains("\"event_type\":\"combat\"", StringComparison.Ordinal))
                    combats++;
            }

            _eventCount = events;
            _decisionCount = decisions;
            _combatCount = combats;
        }
        catch
        {
            // keep in-memory values
        }
    }

    private static void TryAutoCloseTerminalRun(GameState state)
    {
        if (_runId == null || _runFinishedLogged)
            return;

        var terminal = DetectTerminalStatus(state);
        if (terminal == null)
            return;

        UpdateDecisionAttribution(state, forceSnapshot: true);
        EmitEvent(new
        {
            event_type = "run_finished",
            run_id = _runId,
            timestamp = DateTimeOffset.UtcNow,
            status = terminal,
            state = ToSnapshot(state)
        });
        _runFinishedLogged = true;
        _runOutcome = terminal;
        WriteSummary(state, terminal);
        ClearActiveRunPointer();
    }

    private static string? DetectTerminalStatus(GameState state)
    {
        if (state.Hp is <= 0)
            return "defeat";

        var screen = state.CurrentScreen ?? "";
        // Do not use bare "win" — it matches "Window" in many Godot UI paths and falsely ends the run.
        if (screen.Contains("victory", StringComparison.OrdinalIgnoreCase) ||
            screen.Contains("credits", StringComparison.OrdinalIgnoreCase) ||
            screen.Contains("RunComplete", StringComparison.OrdinalIgnoreCase) ||
            screen.Contains("run_complete", StringComparison.OrdinalIgnoreCase) ||
            screen.Contains("NeoVictory", StringComparison.OrdinalIgnoreCase))
            return "victory";

        if (screen.Contains("defeat", StringComparison.OrdinalIgnoreCase) ||
            screen.Contains("death", StringComparison.OrdinalIgnoreCase) ||
            screen.Contains("gameover", StringComparison.OrdinalIgnoreCase) ||
            screen.Contains("GameOver", StringComparison.OrdinalIgnoreCase))
            return "defeat";

        return null;
    }

    private static void EmitEvent(object payload)
    {
        if (_eventsPath == null)
            return;
        var line = JsonSerializer.Serialize(payload, JsonOptions);
        File.AppendAllText(_eventsPath, line + Environment.NewLine);
        _eventCount++;

        if (ContextCoachLogging.Verbose)
            Log.Info($"[ContextCoach][run-log] {line}");
    }

    private static string BuildDecisionFingerprint(string type, IReadOnlyList<string> candidates, GameState state)
    {
        var floor = state.Floor?.ToString() ?? "?";
        var screen = state.CurrentScreen ?? "?";
        return $"{type}|{floor}|{screen}|{string.Join(",", candidates.OrderBy(x => x, StringComparer.Ordinal))}";
    }

    private static bool IsCombatScreen(string? screen) => CombatScreenHeuristic.PathLooksLikeCombat(screen);

    private static string GuessEncounterName(string? screen)
    {
        if (string.IsNullOrWhiteSpace(screen))
            return "unknown";
        var seg = screen.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(seg) ? "unknown" : seg!;
    }

    private static string? ReadEngineVersion()
    {
        try
        {
            var sts2Asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase));
            return sts2Asm?.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadSeed()
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase));
            if (asm == null) return null;
            var smType = asm.GetType("MegaCrit.Sts2.Core.Saves.SaveManager");
            var instance = smType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var load = smType?.GetMethod("LoadRunSave", Type.EmptyTypes);
            var readResult = load?.Invoke(instance, null);
            if (readResult == null) return null;
            var run = readResult.GetType().GetProperty("SaveData")?.GetValue(readResult);
            if (run == null) return null;
            var t = run.GetType();
            var seedVal = t.GetProperty("Seed")?.GetValue(run)
                          ?? t.GetProperty("RunSeed")?.GetValue(run)
                          ?? t.GetProperty("SeedValue")?.GetValue(run);
            return seedVal?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static void PruneDecisionAttributionDictionary(DateTimeOffset now)
    {
        var maxOpen = TimeSpan.FromHours(8);
        var maxClosed = TimeSpan.FromMinutes(45);
        List<string>? remove = null;
        foreach (var kv in DecisionAttribution)
        {
            var age = now - kv.Value.CreatedAt;
            if (kv.Value.Closed && age > maxClosed)
                (remove ??= []).Add(kv.Key);
            else if (!kv.Value.Closed && age > maxOpen)
                (remove ??= []).Add(kv.Key);
        }

        if (remove == null)
            return;
        foreach (var k in remove)
            DecisionAttribution.Remove(k);
    }

    private static void UpdateDecisionAttribution(GameState state, bool forceSnapshot = false)
    {
        if (DecisionAttribution.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        PruneDecisionAttributionDictionary(now);

        const double minSecondsBetweenUpdates = 12;
        foreach (var kv in DecisionAttribution.ToArray())
        {
            var d = kv.Value;
            if (d.Closed)
                continue;

            if (!forceSnapshot)
            {
                var progressedFloor = state.Floor is int nf && d.LastEmittedFloor is int lf && nf > lf;
                if (d.LastOutcomeEmitAt is { } lastEmit)
                {
                    if ((now - lastEmit).TotalSeconds < minSecondsBetweenUpdates && !progressedFloor)
                        continue;

                    if ((now - lastEmit).TotalSeconds >= minSecondsBetweenUpdates &&
                        Nullable.Equals(state.Floor, d.LastEmittedFloor) &&
                        Nullable.Equals(state.Hp, d.LastEmittedHp) &&
                        Nullable.Equals(state.Gold, d.LastEmittedGold) &&
                        Nullable.Equals(state.Deck?.Count, d.LastEmittedDeckSize) &&
                        !progressedFloor)
                        continue;
                }
            }

            var floorDelta = state.Floor is int sf && d.StartFloor is int st ? sf - st : 0;
            var hpDelta = state.Hp is int hp && d.StartHp is int shp ? hp - shp : (int?)null;
            var goldDelta = state.Gold is int g && d.StartGold is int sg ? g - sg : (int?)null;
            var deckDelta = state.Deck?.Count is int ds && d.StartDeckSize is int sd ? ds - sd : (int?)null;

            EmitEvent(new
            {
                event_type = "decision_outcome_update",
                run_id = _runId,
                decision_id = d.DecisionId,
                timestamp = now,
                floors_since_decision = floorDelta,
                hp_delta = hpDelta,
                gold_delta = goldDelta,
                deck_size_delta = deckDelta,
                state = ToSnapshot(state)
            });

            d.LastOutcomeEmitAt = now;
            d.LastEmittedFloor = state.Floor;
            d.LastEmittedHp = state.Hp;
            d.LastEmittedGold = state.Gold;
            d.LastEmittedDeckSize = state.Deck?.Count;
            d.LastObservedFloor = state.Floor;

            if (forceSnapshot || floorDelta >= 3 || (now - d.CreatedAt).TotalMinutes > 15)
                d.Closed = true;
        }
    }

    private static ExportState ReadExportState()
    {
        var path = ExportStatePath();
        if (!File.Exists(path))
            return new ExportState();
        try
        {
            var raw = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ExportState>(raw, JsonOptions) ?? new ExportState();
        }
        catch
        {
            return new ExportState();
        }
    }

    private static void WriteExportState(ExportState state)
    {
        try
        {
            var path = ExportStatePath();
            var raw = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(path, raw);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] WriteExportState failed: {ex.Message}");
        }
    }

    private static void MarkRunExported(string runId, string zipPath)
    {
        var state = ReadExportState();
        state.ExportedRunIds.Add(runId);
        state.Bundles.Add(new ExportBundle
        {
            BundlePath = zipPath,
            ExportedAt = DateTimeOffset.UtcNow,
            RunIds = [runId]
        });
        WriteExportState(state);
    }

    private static string ExportStatePath() =>
        Path.Combine(ContextCoachConfig.GetLogsRootPath(), "export_state.json");

    private static void AddIfExists(ZipArchive zip, string path, string entryName)
    {
        if (File.Exists(path))
            zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
    }

    private static void AddLlmTranscriptEntries(ZipArchive zip, string runDir, string entryPrefix)
    {
        var llmDir = Path.Combine(runDir, "llm");
        if (!Directory.Exists(llmDir))
            return;
        foreach (var path in Directory.GetFiles(llmDir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            AddIfExists(zip, path, $"{entryPrefix}llm/{name}");
        }
    }

    private static Dictionary<string, int> BuildDeckMultiset(GameState state)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (state.Deck == null)
            return map;

        foreach (var c in state.Deck)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                continue;
            map.TryGetValue(c.Name, out var count);
            map[c.Name] = count + 1;
        }

        return map;
    }

    private static string? FindSingleAddedCard(
        IReadOnlyDictionary<string, int> previous,
        IReadOnlyDictionary<string, int> current)
    {
        string? found = null;
        var added = 0;
        foreach (var kv in current)
        {
            previous.TryGetValue(kv.Key, out var prev);
            var delta = kv.Value - prev;
            if (delta <= 0) continue;
            added += delta;
            if (found == null)
                found = kv.Key;
            if (added > 1)
                return null;
        }

        return added == 1 ? found : null;
    }

    private sealed class PendingDecision
    {
        public required string DecisionId { get; init; }
        public required List<string> Candidates { get; init; }
        public int? Floor { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class DecisionAttributionState
    {
        public required string DecisionId { get; init; }
        public required string DecisionType { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public int? StartFloor { get; init; }
        public int? StartHp { get; init; }
        public int? StartGold { get; init; }
        public int? StartDeckSize { get; init; }
        public int? LastObservedFloor { get; set; }
        public DateTimeOffset? LastOutcomeEmitAt { get; set; }
        public int? LastEmittedFloor { get; set; }
        public int? LastEmittedHp { get; set; }
        public int? LastEmittedGold { get; set; }
        public int? LastEmittedDeckSize { get; set; }
        public bool Closed { get; set; }
    }

    private sealed class ExportState
    {
        public HashSet<string> ExportedRunIds { get; set; } = new(StringComparer.Ordinal);
        public List<ExportBundle> Bundles { get; set; } = [];
    }

    private sealed class ExportBundle
    {
        public required string BundlePath { get; init; }
        public required DateTimeOffset ExportedAt { get; init; }
        public required List<string> RunIds { get; init; }
    }
}
