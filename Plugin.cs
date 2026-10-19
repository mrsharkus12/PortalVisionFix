using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ULTRAKILL.Portal;
using ULTRAKILL.Portal.Native;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace PortalVisionFix;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "mrsharksu.portalvisionfix";
    public const string Name = "PortalVisionFix";
    public const string Version = "1.0";

    internal static ManualLogSource Log;
    internal static ConfigEntry<int> MaxPortalDepth;
    internal static ConfigEntry<int> MaxSequences;
    internal static ConfigEntry<bool> PruneImpossible;

    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;
        MaxPortalDepth = Config.Bind("Vision", "MaxPortalDepth", 4,
            new ConfigDescription("How many portals in a row enemies can see through (vanilla: 2).", new AcceptableValueRange<int>(1, 6)));
        MaxSequences = Config.Bind("Vision", "MaxSequences", 2048,
            new ConfigDescription("Upper bound on portal sequences per frame. Vision cost scales with sequences x targets.", new AcceptableValueRange<int>(16, 16384)));
        PruneImpossible = Config.Bind("Vision", "PruneImpossibleSequences", true,
            "Use PortalVisionJob to drop chains where one portal cannot see the next. Disabling grows the tree as N^depth.");

        _harmony = new Harmony(Guid);
        _harmony.PatchAll(typeof(Plugin).Assembly);
        foreach (var method in _harmony.GetPatchedMethods())
        {
            Log.LogInfo($"Patched {method.DeclaringType?.FullName}.{method.Name}");
        }
        Log.LogInfo($"{Name} {Version} loaded (depth {MaxPortalDepth.Value}, cap {MaxSequences.Value}, prune {PruneImpossible.Value})");
    }

    private void OnDestroy()
    {

    }
}

[HarmonyPatch(typeof(PortalScene), nameof(PortalScene.Sync))]
internal static class SyncPatch
{
    private static NativeArray<PortalHandle> _handles;
    private static NativeArray<float4x4> _transforms;
    private static NativeArray<PortalVertices> _verts;

    private static readonly List<PortalHandleSequence> _sequences = new List<PortalHandleSequence>();
    private static readonly List<int> _parent = new List<int>();
    private static readonly List<int> _portal = new List<int>();
    private static readonly List<long> _keys = new List<long>();
    private static bool[] _possible = Array.Empty<bool>();
    private static int _builtDepth = -1;
    private static int _builtCap = -1;
    private static bool _builtPrune;
    private static bool _capped;

    private static readonly HashSet<string> _logged = new HashSet<string>();

    private static void LogOnce(string message)
    {
        if (_logged.Add(message))
        {
            Plugin.Log.LogInfo(message);
        }
    }

    private static void Postfix(PortalScene __instance)
    {
        try
        {
            Apply(__instance);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private static void Apply(PortalScene scene)
    {
        if (!scene.nativeScene.valid || !scene.nativeScene.portals.IsCreated)
        {
            return;
        }
        NativeList<NativePortal> portals = scene.nativeScene.portals;
        int n = portals.Length;
        if (n == 0)
        {
            return;
        }

        bool prune = Plugin.PruneImpossible.Value && scene.visionPossible.Length == n * n;
        if (prune)
        {
            RunVisionJob(scene, portals, n);
        }

        if (NeedsRebuild(scene, portals, n, prune))
        {
            Rebuild(scene, portals, n, prune);
            LogOnce($"Vision sequences rebuilt: {n} portal sides, depth {_builtDepth}, {_sequences.Count} sequences" +
                    (_capped ? $" (capped at {_builtCap})" : ""));
        }

        int count = _sequences.Count;
        scene.portalSequences.Clear();
        scene.portalSequences.AddRange(_sequences);
        scene.culledSequences.Clear();
        for (int i = 0; i < count; i++)
        {
            scene.culledSequences.Add(false);
        }
        if (scene.sequenceMatrices.IsCreated)
        {
            scene.sequenceMatrices.Dispose();
        }
        var matrices = new NativeArray<float4x4>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        matrices[0] = float4x4.identity;
        for (int i = 1; i < count; i++)
        {
            matrices[i] = math.mul(portals[_portal[i]].travelMatrix, matrices[_parent[i]]);
        }
        scene.sequenceMatrices = matrices;
    }

    private static void RunVisionJob(PortalScene scene, NativeList<NativePortal> portals, int n)
    {
        EnsureBuffers(n);
        for (int i = 0; i < n; i++)
        {
            NativePortal p = portals[i];
            _handles[i] = p.handle;
            _transforms[i] = p.transform.toWorld;
            _verts[i] = p.vertices;
        }
        new PortalVisionJob
        {
            handles = _handles.GetSubArray(0, n),
            transforms = _transforms.GetSubArray(0, n),
            verts = _verts.GetSubArray(0, n),
            visionPossible = scene.visionPossible.AsArray()
        }.Run(n);
    }

    private static bool NeedsRebuild(PortalScene scene, NativeList<NativePortal> portals, int n, bool prune)
    {
        bool changed = _builtDepth != Plugin.MaxPortalDepth.Value || _builtCap != Plugin.MaxSequences.Value || _builtPrune != prune || _keys.Count != n;
        if (!changed)
        {
            for (int i = 0; i < n; i++)
            {
                if (_keys[i] != portals[i].handle.PackedKey)
                {
                    changed = true;
                    break;
                }
            }
        }
        if (prune)
        {
            NativeArray<bool> possible = scene.visionPossible.AsArray();
            if (_possible.Length != n * n)
            {
                changed = true;
            }
            else if (!changed)
            {
                for (int i = 0; i < _possible.Length; i++)
                {
                    if (_possible[i] != possible[i])
                    {
                        changed = true;
                        break;
                    }
                }
            }
        }
        return changed;
    }

    private static void Rebuild(PortalScene scene, NativeList<NativePortal> portals, int n, bool prune)
    {
        _builtDepth = Plugin.MaxPortalDepth.Value;
        _builtCap = Plugin.MaxSequences.Value;
        _builtPrune = prune;
        _capped = false;

        _keys.Clear();
        for (int i = 0; i < n; i++)
        {
            _keys.Add(portals[i].handle.PackedKey);
        }
        if (prune)
        {
            if (_possible.Length != n * n)
            {
                _possible = new bool[n * n];
            }
            scene.visionPossible.AsArray().CopyTo(_possible);
        }

        _sequences.Clear();
        _parent.Clear();
        _portal.Clear();
        _sequences.Add(default);
        _parent.Add(-1);
        _portal.Add(-1);

        int levelStart = 0;
        int levelEnd = 1;
        for (int depth = 1; depth <= _builtDepth && !_capped; depth++)
        {
            for (int node = levelStart; node < levelEnd && !_capped; node++)
            {
                int last = _portal[node];

                int exit = last < 0 ? -1 : (portals[last].handle.side == PortalSide.Enter ? last + 1 : last - 1);
                for (int j = 0; j < n; j++)
                {
                    if (exit >= 0)
                    {
                        if (j == exit)
                        {
                            continue;
                        }
                        if (prune && exit < n && !_possible[exit * n + j])
                        {
                            continue;
                        }
                    }
                    if (_sequences.Count >= _builtCap)
                    {
                        _capped = true;
                        break;
                    }
                    _sequences.Add(_sequences[node].Append(portals[j].handle));
                    _parent.Add(node);
                    _portal.Add(j);
                }
            }
            levelStart = levelEnd;
            levelEnd = _sequences.Count;
        }
    }

    private static void EnsureBuffers(int n)
    {
        if (_handles.IsCreated && _handles.Length >= n)
        {
            return;
        }
        if (_handles.IsCreated) _handles.Dispose();
        if (_transforms.IsCreated) _transforms.Dispose();
        if (_verts.IsCreated) _verts.Dispose();
        int capacity = Math.Max(16, n * 2);
        _handles = new NativeArray<PortalHandle>(capacity, Allocator.Persistent);
        _transforms = new NativeArray<float4x4>(capacity, Allocator.Persistent);
        _verts = new NativeArray<PortalVertices>(capacity, Allocator.Persistent);
    }
}
