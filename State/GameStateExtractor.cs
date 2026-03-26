using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Sts2ContextCoach.Data;
using Sts2ContextCoach.Diagnostics;

namespace Sts2ContextCoach.State;

public static class GameStateExtractor
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;

    private static List<object>? _cachedStaticRoots;

    public static void ClearReflectionCaches() => _cachedStaticRoots = null;

    /// <summary>Per-card snapshot: shared global run state + this row’s reward context. Cheap every tick.</summary>
    public static GameState ExtractForCard(NCard? rewardAnchor) => GameStateCache.GetStateForCard(rewardAnchor);

    /// <summary>Build global snapshot: prefer <c>SaveManager.Instance.LoadRunSave()</c>, fallback to generic reflection.</summary>
    /// <param name="provenance"><c>SaveManager</c> or <c>ReflectionFallback</c> (for logs).</param>
    public static GameState BuildGlobalReflectionState(out string provenance)
    {
        provenance = "ReflectionFallback";
        var state = new GameState();
        try
        {
            if (TryFillFromSaveManager(state))
            {
                var finalProvenance = "SaveManager";
                // In multiplayer, save-backed CharacterId can lag/stale; prefer live runtime character when available.
                if (TryResolveLiveCharacterFromStaticRoots(typeof(NCard).Assembly, out var liveChar) &&
                    !string.IsNullOrWhiteSpace(liveChar) &&
                    !string.Equals(state.Character, liveChar, StringComparison.OrdinalIgnoreCase))
                {
                    ContextCoachLogging.VerboseInfo(
                        $"Overriding SaveManager character '{state.Character ?? "?"}' with live runtime character '{liveChar}'.");
                    state.Character = liveChar;
                    finalProvenance = "SaveManager+LiveChar";
                }

                provenance = finalProvenance;
                WarnIfIncomplete(state);
                return state;
            }

            ContextCoachLogging.VerboseInfo("SaveManager path did not yield state; trying static-root reflection.");
            var asm = typeof(NCard).Assembly;
            foreach (var root in GetOrBuildStaticRoots(asm))
            {
                TryFillFromRoot(root, state);
                if (state.Gold != null && state.Deck is { Count: > 0 })
                    break;
            }

            ContextCoachLogging.VerboseInfo(
                $"Reflection fallback done: deck={state.Deck?.Count ?? 0} gold={state.Gold?.ToString() ?? "null"}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] GameState extraction failed: {ex}");
        }

        WarnIfIncomplete(state);
        return state;
    }

    /// <summary>Backward-compatible overload; provenance discarded.</summary>
    public static GameState BuildGlobalReflectionState() => BuildGlobalReflectionState(out _);

    /// <summary>Reads <c>SerializableRun</c> / <c>SerializablePlayer</c> from the game’s save pipeline (same data the run uses).</summary>
    private static bool TryFillFromSaveManager(GameState state)
    {
        try
        {
            var asm = typeof(NCard).Assembly;
            var saveManagerType = asm.GetType("MegaCrit.Sts2.Core.Saves.SaveManager");
            if (saveManagerType == null)
            {
                ContextCoachLogging.VerboseInfo("SaveManager: type MegaCrit.Sts2.Core.Saves.SaveManager not found.");
                return false;
            }

            var instance = saveManagerType.GetProperty("Instance", Flags)?.GetValue(null);
            if (instance == null)
            {
                ContextCoachLogging.VerboseInfo("SaveManager.Instance returned null (menu / not initialized?).");
                return false;
            }

            var load = saveManagerType.GetMethod("LoadRunSave", Type.EmptyTypes);
            if (load == null)
            {
                ContextCoachLogging.VerboseInfo("SaveManager.LoadRunSave() not found.");
                return false;
            }

            var readResult = load.Invoke(instance, null);
            if (readResult == null)
            {
                ContextCoachLogging.VerboseInfo("LoadRunSave returned null.");
                return false;
            }

            var rt = readResult.GetType();
            var ok = rt.GetProperty("Success")?.GetValue(readResult) is true;
            var run = rt.GetProperty("SaveData")?.GetValue(readResult);
            if (run == null)
            {
                var status = rt.GetProperty("Status")?.GetValue(readResult);
                var err = rt.GetProperty("ErrorMessage")?.GetValue(readResult) as string;
                ContextCoachLogging.VerboseInfo($"LoadRunSave SaveData null (Success={ok}, Status={status}, Err={err}).");
                return false;
            }

            if (!ok)
            {
                var status = rt.GetProperty("Status")?.GetValue(readResult);
                var err = rt.GetProperty("ErrorMessage")?.GetValue(readResult) as string;
                Log.Warn($"[ContextCoach] LoadRunSave Success=false (Status={status}, {err}); still mapping SaveData.");
            }
            else
            {
                ContextCoachLogging.VerboseInfo("LoadRunSave Success=true.");
            }

            MapSerializableRun(run, state);
            var useful = state.Deck is { Count: > 0 } || state.Gold != null || state.Hp != null;
            if (!useful)
                ContextCoachLogging.VerboseInfo("SaveData mapped but deck empty and gold/hp null (unexpected layout?).");
            return useful;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] SaveManager snapshot failed: {ex.Message}");
            return false;
        }
    }

    private static void MapSerializableRun(object run, GameState state)
    {
        var t = run.GetType();
        if (t.GetProperty("Ascension")?.GetValue(run) is int asc)
            state.Ascension = asc;
        if (t.GetProperty("CurrentActIndex")?.GetValue(run) is int actIdx)
            state.Act = actIdx + 1;

        if (t.GetProperty("VisitedMapCoords")?.GetValue(run) is IEnumerable visited)
        {
            var n = 0;
            foreach (var _ in visited) n++;
            if (n > 0) state.Floor = n;
        }

        if (t.GetProperty("Players")?.GetValue(run) is not IEnumerable players)
            return;

        object? player = null;
        foreach (var p in players)
        {
            player = p;
            break;
        }

        if (player == null) return;

        var pt = player.GetType();
        if (pt.GetProperty("Gold")?.GetValue(player) is int gold)
            state.Gold = gold;
        if (pt.GetProperty("CurrentHp")?.GetValue(player) is int hp)
            state.Hp = hp;
        if (pt.GetProperty("MaxHp")?.GetValue(player) is int maxHp)
            state.MaxHp = maxHp;
        if (pt.GetProperty("MaxEnergy")?.GetValue(player) is int maxEnergy)
            state.MaxEnergy = maxEnergy;

        var charEntry = CardIdNormalizer.FromModelIdEntry(ModelIdEntry(pt.GetProperty("CharacterId")?.GetValue(player)));
        if (!string.IsNullOrEmpty(charEntry))
            state.Character = charEntry;

        var deck = MapSerializableCards(pt.GetProperty("Deck")?.GetValue(player));
        if (deck.Count > 0)
            state.Deck = deck;

        var relics = MapSerializableRelics(pt.GetProperty("Relics")?.GetValue(player));
        if (relics.Count > 0)
            state.Relics = relics;
    }

    private static string? ModelIdEntry(object? modelId)
    {
        if (modelId == null) return null;
        if (modelId.GetType().GetProperty("Entry", Flags)?.GetValue(modelId) is string e && !string.IsNullOrEmpty(e))
            return e;
        return modelId.ToString();
    }

    private static List<CardInstance> MapSerializableCards(object? deckObj)
    {
        var list = new List<CardInstance>();
        if (deckObj is not IEnumerable en) return list;

        foreach (var item in en)
        {
            if (item == null) continue;
            var it = item.GetType();
            var raw = ModelIdEntry(it.GetProperty("Id")?.GetValue(item));
            var name = CardIdNormalizer.FromModelIdEntry(raw);
            if (string.IsNullOrEmpty(name)) continue;
            var upgraded = it.GetProperty("CurrentUpgradeLevel")?.GetValue(item) is int lvl && lvl > 0;
            list.Add(new CardInstance { Name = name, Upgraded = upgraded });
        }

        return list;
    }

    private static List<string> MapSerializableRelics(object? relicObj)
    {
        var list = new List<string>();
        if (relicObj is not IEnumerable en) return list;

        foreach (var item in en)
        {
            if (item == null) continue;
            var raw = ModelIdEntry(item.GetType().GetProperty("Id")?.GetValue(item));
            var name = CardIdNormalizer.FromModelIdEntry(raw);
            if (!string.IsNullOrEmpty(name))
                list.Add(name);
        }

        return list;
    }

    public static GameState MergePerCard(GameState global, NCard? anchor)
    {
        var state = new GameState
        {
            Character = global.Character,
            Hp = global.Hp,
            MaxHp = global.MaxHp,
            Gold = global.Gold,
            Deck = global.Deck,
            Relics = global.Relics,
            Act = global.Act,
            Floor = global.Floor,
            Ascension = global.Ascension,
            MaxEnergy = global.MaxEnergy
        };

        if (anchor == null) return state;

        state.CurrentScreen = DescribeScreen(anchor);
        state.RewardCards = CollectNeighborRewardCards(anchor);
        return state;
    }

    private static List<object> GetOrBuildStaticRoots(Assembly asm)
    {
        if (_cachedStaticRoots != null) return _cachedStaticRoots;

        var list = new List<object>();
        var seen = new HashSet<long>();
        foreach (var root in FindStaticRoots(asm))
        {
            var id = (long)RuntimeHelpers.GetHashCode(root);
            if (!seen.Add(id)) continue;
            list.Add(root);
        }

        _cachedStaticRoots = list;
        Log.Info($"[ContextCoach] Cached {list.Count} static reflection root(s).");
        return list;
    }

    private static void WarnIfIncomplete(GameState state)
    {
        if (state.Deck == null)
            Log.Warn("[ContextCoach] GameState: deck unresolved (reflection)");
        if (state.Relics == null)
            Log.Warn("[ContextCoach] GameState: relics unresolved (reflection)");
        if (state.Gold == null)
            Log.Warn("[ContextCoach] GameState: gold unresolved (reflection)");
        if (state.Hp == null)
            Log.Warn("[ContextCoach] GameState: hp unresolved (reflection)");
        if (state.Act == null && state.Floor == null)
            Log.Warn("[ContextCoach] GameState: act/floor unresolved (reflection)");
    }

    private static string DescribeScreen(NCard card)
    {
        var parts = new List<string>();
        Node? n = card.GetParent();
        while (n != null && parts.Count < 12)
        {
            parts.Add(n.Name.ToString());
            n = n.GetParent();
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static List<CardInstance>? CollectNeighborRewardCards(NCard anchor)
    {
        var parent = anchor.GetParent();
        if (parent == null) return null;

        var list = new List<CardInstance>();
        foreach (var child in parent.GetChildren())
        {
            if (child is not NCard nc) continue;
            var name = CardModelReflection.GetInternalName(nc);
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new CardInstance
            {
                Name = name,
                Upgraded = CardModelReflection.IsUpgraded(nc)
            });
        }

        return list.Count > 0 ? list : null;
    }

    private static IEnumerable<object> FindStaticRoots(Assembly asm)
    {
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            foreach (var name in new[] { "Instance", "Current", "Singleton", "Shared" })
            {
                object? value = null;
                var prop = type.GetProperty(name, Flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                {
                    try { value = prop.GetValue(null); } catch { /* ignored */ }
                }

                if (value == null)
                {
                    var field = type.GetField(name, Flags);
                    if (field != null)
                    {
                        try { value = field.GetValue(null); } catch { /* ignored */ }
                    }
                }

                if (value != null)
                    yield return value;
            }
        }
    }

    private static void TryFillFromRoot(object root, GameState state)
    {
        var visited = new HashSet<int>();
        var queue = new Queue<object>();
        queue.Enqueue(root);
        var budget = 96;

        while (queue.Count > 0 && budget-- > 0)
        {
            var obj = queue.Dequeue();
            var id = RuntimeHelpers.GetHashCode(obj);
            if (!visited.Add(id)) continue;

            TryMapScalars(obj, state);
            TryMapDeck(obj, state);
            TryMapRelics(obj, state);
            TryMapCharacter(obj, state);

            foreach (var nested in EnumerateNested(obj, perObjectLimit: 28))
                queue.Enqueue(nested);
        }
    }

    private static void TryMapCharacter(object obj, GameState state)
    {
        if (state.Character != null) return;
        if (TryReadCharacterId(obj, out var character))
        {
            state.Character = character;
            return;
        }

        var t = obj.GetType();
        if (t.Name.Contains("Player", StringComparison.Ordinal) ||
            t.Name.Contains("Hero", StringComparison.Ordinal) ||
            t.Name.Contains("Character", StringComparison.Ordinal))
        {
            state.Character ??= t.Name;
        }
    }

    private static void TryMapScalars(object obj, GameState state)
    {
        foreach (var p in obj.GetType().GetProperties(Flags))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            object? value;
            try
            {
                value = p.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value == null) continue;
            var name = p.Name;

            switch (value)
            {
                case int i:
                    MaybeSetInt(state, name, i);
                    break;
                case short s:
                    MaybeSetInt(state, name, s);
                    break;
                case long l when l <= int.MaxValue && l >= int.MinValue:
                    MaybeSetInt(state, name, (int)l);
                    break;
                case float f when name.Contains("Hp", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("Health", StringComparison.OrdinalIgnoreCase):
                    if (state.Hp == null) state.Hp = (int)f;
                    break;
                case double d when name.Contains("Hp", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("Health", StringComparison.OrdinalIgnoreCase):
                    if (state.Hp == null) state.Hp = (int)d;
                    break;
            }
        }
    }

    private static void MaybeSetInt(GameState state, string name, int value)
    {
        if (name.Contains("Gold", StringComparison.OrdinalIgnoreCase) && state.Gold == null)
            state.Gold = value;
        else if ((name.Contains("Hp", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("Health", StringComparison.OrdinalIgnoreCase)) &&
                 !name.Contains("Max", StringComparison.OrdinalIgnoreCase) && state.Hp == null)
            state.Hp = value;
        else if (name.Contains("MaxHp", StringComparison.OrdinalIgnoreCase) && state.MaxHp == null)
            state.MaxHp = value;
        else if (name.Contains("Ascension", StringComparison.OrdinalIgnoreCase) && state.Ascension == null)
            state.Ascension = value;
        else if (IsActField(name) && state.Act == null)
            state.Act = value;
        else if (IsFloorField(name) && state.Floor == null)
            state.Floor = value;
        else if (name.Contains("Energy", StringComparison.OrdinalIgnoreCase) &&
                 name.Contains("Max", StringComparison.OrdinalIgnoreCase) &&
                 state.MaxEnergy == null)
            state.MaxEnergy = value;
    }

    private static bool IsActField(string name)
    {
        return name.Equals("Act", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ActIndex", StringComparison.OrdinalIgnoreCase)
               || name.Equals("CurrentAct", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ActNumber", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFloorField(string name)
    {
        return name.Equals("Floor", StringComparison.OrdinalIgnoreCase)
               || name.Equals("FloorNumber", StringComparison.OrdinalIgnoreCase)
               || name.Equals("MapFloor", StringComparison.OrdinalIgnoreCase)
               || name.Equals("DungeonFloor", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryMapDeck(object obj, GameState state)
    {
        if (state.Deck is { Count: > 0 }) return;

        foreach (var p in obj.GetType().GetProperties(Flags))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            var name = p.Name;
            if (!name.Contains("Deck", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("DrawPile", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("MasterDeck", StringComparison.OrdinalIgnoreCase))
                continue;

            object? value;
            try
            {
                value = p.GetValue(obj);
            }
            catch
            {
                continue;
            }

            var deck = TryCoerceDeck(value);
            if (deck is { Count: > 0 })
            {
                state.Deck = deck;
                return;
            }
        }
    }

    private static void TryMapRelics(object obj, GameState state)
    {
        if (state.Relics is { Count: > 0 }) return;

        foreach (var p in obj.GetType().GetProperties(Flags))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            var name = p.Name;
            if (!name.Contains("Relic", StringComparison.OrdinalIgnoreCase)) continue;

            object? value;
            try
            {
                value = p.GetValue(obj);
            }
            catch
            {
                continue;
            }

            var relics = TryCoerceStringList(value);
            if (relics is { Count: > 0 })
            {
                state.Relics = relics;
                return;
            }
        }
    }

    private static List<CardInstance>? TryCoerceDeck(object? value)
    {
        if (value is not IEnumerable enumerable) return null;
        var list = new List<CardInstance>();
        foreach (var item in enumerable)
        {
            if (item == null) continue;
            var name = item.GetType().Name;
            if (string.IsNullOrEmpty(name) || name is "Object" or "ValueType") continue;

            var upgraded = false;
            var t = item.GetType();
            foreach (var pn in new[] { "IsUpgraded", "Upgraded" })
            {
                var prop = t.GetProperty(pn, Flags);
                if (prop?.PropertyType == typeof(bool) && prop.CanRead)
                {
                    try
                    {
                        upgraded = prop.GetValue(item) is true;
                    }
                    catch
                    {
                        // ignored
                    }

                    break;
                }
            }

            list.Add(new CardInstance { Name = name, Upgraded = upgraded });
        }

        return list.Count > 0 ? list : null;
    }

    private static List<string>? TryCoerceStringList(object? value)
    {
        if (value is not IEnumerable enumerable) return null;
        var list = new List<string>();
        foreach (var item in enumerable)
        {
            if (item == null) continue;
            var t = item.GetType();
            list.Add(t.Name);

            if (list.Count > 40) break;
        }

        return list.Count > 0 ? list : null;
    }

    private static IEnumerable<object> EnumerateNested(object obj, int perObjectLimit)
    {
        var n = 0;
        foreach (var p in obj.GetType().GetProperties(Flags))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            if (n++ > perObjectLimit) yield break;

            object? val;
            try
            {
                val = p.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (val == null) continue;
            var t = val.GetType();
            if (t.IsPrimitive || val is string) continue;
            if (val is IEnumerable) continue;

            yield return val;
        }
    }

    private static bool TryResolveLiveCharacterFromStaticRoots(Assembly asm, out string character)
    {
        character = "";
        var visited = new HashSet<int>();
        var queue = new Queue<object>();
        foreach (var root in GetOrBuildStaticRoots(asm))
            queue.Enqueue(root);

        var budget = 256;
        while (queue.Count > 0 && budget-- > 0)
        {
            var obj = queue.Dequeue();
            var id = RuntimeHelpers.GetHashCode(obj);
            if (!visited.Add(id)) continue;

            if (TryReadCharacterId(obj, out var found))
            {
                character = found;
                return true;
            }

            foreach (var nested in EnumerateNested(obj, perObjectLimit: 20))
                queue.Enqueue(nested);
        }

        return false;
    }

    private static bool TryReadCharacterId(object obj, out string character)
    {
        character = "";
        var t = obj.GetType();
        object? raw = null;

        foreach (var name in new[] { "CharacterId", "Character", "CharacterType", "SelectedCharacterId" })
        {
            try
            {
                raw = t.GetProperty(name, Flags)?.GetValue(obj) ?? t.GetField(name, Flags)?.GetValue(obj);
            }
            catch
            {
                raw = null;
            }

            if (raw == null) continue;
            var entry = CardIdNormalizer.FromModelIdEntry(ModelIdEntry(raw));
            if (!string.IsNullOrWhiteSpace(entry))
            {
                character = entry;
                return true;
            }
        }

        return false;
    }
}
