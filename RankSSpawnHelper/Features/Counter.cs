﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.GeneratedSheets;
using RankSSpawnHelper.Models;

namespace RankSSpawnHelper.Features;

internal partial class Counter : IDisposable
{
    private readonly Dictionary<ushort, Dictionary<string, uint>> _conditionsMob = new()
    {
            { 961, new Dictionary<string, uint>() }, // 鸟蛋
            { 959, new Dictionary<string, uint>() }, // 叹息海
            { 957, new Dictionary<string, uint>() }, // 萨维奈岛
            { 814, new Dictionary<string, uint>() }, // 棉花
            { 813, new Dictionary<string, uint>() }, // Lakeland
            { 817, new Dictionary<string, uint>() }, // 拉凯提卡大森林
            { 621, new Dictionary<string, uint>() }, // 湖区
            { 613, new Dictionary<string, uint>() }, // 红玉海
            { 612, new Dictionary<string, uint>() }, // 边区
            { 402, new Dictionary<string, uint>() }, // 魔大陆
            { 400, new Dictionary<string, uint>() }, // 翻云雾海
            { 147, new Dictionary<string, uint>() }  // 北萨
    };

    private readonly Dictionary<string, Tracker> _localTracker     = new();
    private readonly Dictionary<string, Tracker> _networkedTracker = new();

    private readonly Stopwatch _timer = new();

    public unsafe Counter()
    {
        SignatureHelper.Initialise(this);

        InitializeData();
        ActorControlSelf.Enable();
        SystemLogMessage.Enable();
        InventoryTransactionDiscard.Enable();
        ProcessSpawnNpcPacket.Enable();
        ProcessOpenTreasure.Enable();
        // UseActionHook = Hook<UseActionDelegate>.FromFunctionPointerVariable((IntPtr)ActionManager.Addresses.UseAction.Value, Detour_UseAction);
        UseActionHook.Enable();
        DalamudApi.Framework.Update += Framework_Update;

        DalamudApi.Condition.ConditionChange += Condition_OnConditionChange;
    }

    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
    [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 8B F2 49 8B D8 41 0F B6 50 ?? 48 8B F9 E8 ?? ?? ?? ?? 48 8D 44 24 ?? C7 44 24 ?? ?? ?? ?? ?? 41 B8 ?? ?? ?? ?? 66 0F 1F 84 00 ?? ?? ?? ?? 48 8D 80 ?? ?? ?? ?? 0F 10 03 0F 10 4B ?? 48 8D 9B ?? ?? ?? ?? 0F 11 40 ?? 0F 10 43 ?? 0F 11 48 ?? 0F 10 4B ?? 0F 11 40 ?? 0F 10 43 ?? 0F 11 48 ?? 0F 10 4B ?? 0F 11 40 ?? 0F 10 43 ?? 0F 11 48 ?? 0F 10 4B ?? 0F 11 40 ?? 0F 11 48 ?? 49 83 E8 ?? 75 ?? 4C 8D 44 24", DetourName = nameof(Detour_ProcessSpawnNpcPacket))]
    private Hook<ProcessSpawnNpcDelegate> ProcessSpawnNpcPacket { get; init; } = null!;

    public void Dispose()
    {
        ProcessSpawnNpcPacket.Dispose();
        ActorControlSelf.Dispose();
        SystemLogMessage.Dispose();
        InventoryTransactionDiscard.Dispose();
        ProcessOpenTreasure.Dispose();
        UseActionHook.Dispose();
        DalamudApi.Condition.ConditionChange -= Condition_OnConditionChange;
        DalamudApi.Framework.Update          -= Framework_Update;
        GC.SuppressFinalize(this);
    }

    private void Framework_Update(Framework framework)
    {
        // check every 5 seconds
        if (_timer.Elapsed < TimeSpan.FromSeconds(5))
            return;

        _timer.Restart();

        if (_localTracker.Count == 0)
            return;

        foreach (var (k, v) in _localTracker)
        {
            var delta = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeSeconds(v.lastUpdateTime);
            if (delta.TotalMinutes <= Plugin.Configuration.TrackerClearThreshold)
                continue;

            _networkedTracker.Remove(k);
            _localTracker.Remove(k);
        }
    }

    public Dictionary<string, Tracker> GetLocalTrackers()
    {
        return _localTracker;
    }

    public Dictionary<string, Tracker> GetNetworkedTrackers()
    {
        return _networkedTracker;
    }

    /*
     * Remove an instance local and networked tracker
     * if instance is an empty string then it will clear the trackers
     */
    public void RemoveInstance(string instance = "")
    {
        if (instance == string.Empty)
        {
            _localTracker.Clear();
            _networkedTracker.Clear();
            return;
        }

        _localTracker.Remove(instance);
        _networkedTracker.Remove(instance);
    }

    public void UpdateNetworkedTracker(string instance, string condition, int value, long time, uint territoryId)
    {
        if (!_networkedTracker.ContainsKey(instance))
        {
            _networkedTracker.Add(instance, new Tracker
            {
                    startTime      = time,
                    lastUpdateTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    counter = new Dictionary<string, int>
                    {
                            { condition, value }
                    },
                    territoryId = territoryId
            });

            PluginLog.Debug($"[SetValue] instance: {instance}, condition: {condition}, value: {value}");
            Plugin.Windows.CounterWindow.IsOpen = true;
            return;
        }

        if (!_networkedTracker.TryGetValue(instance, out var result))
            return;

        if (!result.counter.ContainsKey(condition))
        {
            result.counter.Add(condition, value);
            return;
        }

        result.counter[condition]           = value;
        Plugin.Windows.CounterWindow.IsOpen = true;
        result.lastUpdateTime               = DateTimeOffset.Now.ToUnixTimeSeconds();
        PluginLog.Debug($"[SetValue] instance: {instance}, key: {condition}, value: {value}");
    }

    private void Condition_OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.BetweenAreas51 || value)
            return;

        if (!Plugin.Configuration.TrackKillCount)
            return;

        var currentInstance = Plugin.Managers.Data.Player.GetCurrentTerritory();

        Plugin.Managers.Socket.Main.SendMessage(new AttemptMessage
        {
                Type        = "ChangeArea",
                WorldId     = Plugin.Managers.Data.Player.GetCurrentWorldId(),
                InstanceId  = Plugin.Managers.Data.Player.GetCurrentInstance(),
                TerritoryId = DalamudApi.ClientState.TerritoryType
                // Instance    = currentInstance,
        });

        if (!Plugin.Configuration.TrackerShowCurrentInstance || _localTracker.ContainsKey(currentInstance))
            return;

        Plugin.Windows.CounterWindow.IsOpen = false;
    }

    private unsafe void Detour_ProcessSpawnNpcPacket(nint a1, uint a2, nint packetData)
    {
        ProcessSpawnNpcPacket.Original(a1, a2, packetData);
        if (packetData == nint.Zero)
            return;

        var baseName = *(uint*)(packetData + 0x44);

        if (!Plugin.Managers.Data.SRank.IsSRank(baseName))
            return;

        Plugin.Features.ShowHuntMap.DontRequest();

#if DEBUG || DEBUG_CN
        Plugin.Print("SRank spotted.");
        PluginLog.Warning("SRank spotted.");
#endif

        var territory = DalamudApi.ClientState.TerritoryType;

        if (territory == 960)
        {
            Plugin.Managers.Socket.Main.SendMessage(new AttemptMessage
            {
                    Type        = "WeeEa",
                    WorldId     = Plugin.Managers.Data.Player.GetCurrentWorldId(),
                    InstanceId  = Plugin.Managers.Data.Player.GetCurrentInstance(),
                    TerritoryId = territory,
                    Failed      = false,
                    Names       = Plugin.Windows.WeeEaWindow.GetNameList()
            });
            return;
        }

        if (!_conditionsMob.ContainsKey(territory))
            return;

        var currentInstance = Plugin.Managers.Data.Player.GetCurrentTerritory();
        if (!_networkedTracker.ContainsKey(currentInstance))
            return;

        Plugin.Managers.Socket.Main.SendMessage(new AttemptMessage
        {
                Type        = "ggnore",
                WorldId     = Plugin.Managers.Data.Player.GetCurrentWorldId(),
                InstanceId  = Plugin.Managers.Data.Player.GetCurrentInstance(),
                TerritoryId = DalamudApi.ClientState.TerritoryType,
                Failed      = false
        });
    }

    private void AddToTracker(string key, string targetName, uint targetId, bool isItem = false)
    {
        if (!_localTracker.ContainsKey(key))
        {
            var tracker = new Tracker
            {
                    counter = new Dictionary<string, int>
                    {
                            { targetName, 1 }
                    },
                    lastUpdateTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    startTime      = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    territoryId    = DalamudApi.ClientState.TerritoryType,
                    trackerOwner   = Managers.DataManagers.Player.GetLocalPlayerName()
            };

            _localTracker.Add(key, tracker);
            goto Post;
        }

        if (!_localTracker.TryGetValue(key, out var value))
        {
            PluginLog.Error($"Cannot get value by key {key}");
            return;
        }

        if (!value.counter.ContainsKey(targetName))
        {
            value.counter.Add(targetName, 1);
        }
        else
        {
            value.counter[targetName]++;
        }

        value.lastUpdateTime = DateTimeOffset.Now.ToUnixTimeSeconds();
    Post:
        PluginLog.Debug($"+1 to key \"{key}\" [{targetName}]");

        Plugin.Windows.CounterWindow.IsOpen = true;

        Plugin.Managers.Socket.Main.SendMessage(new CounterMessage
        {
                Type        = "AddData",
                WorldId     = Plugin.Managers.Data.Player.GetCurrentWorldId(),
                InstanceId  = Plugin.Managers.Data.Player.GetCurrentInstance(),
                TerritoryId = DalamudApi.ClientState.TerritoryType,
                StartTime = !GetLocalTrackers().TryGetValue(key, out var currentTracker)
                                    ? DateTimeOffset.Now.ToUnixTimeSeconds()
                                    : currentTracker.startTime,
                Data = new Dictionary<uint, int>
                {
                        { targetId, 1 }
                },
                IsItem = isItem
        });
    }

    private void InitializeData()
    {
        var npcNames = DalamudApi.DataManager.GetExcelSheet<BNpcName>();
        var items    = DalamudApi.DataManager.GetExcelSheet<Item>();

        string GetNpcName(uint row)
        {
            var name = npcNames.GetRow(row).Singular.RawString.ToLower();
            return name;
        }

        string GetItemName(uint row)
        {
            var name = items.GetRow(row).Singular.RawString.ToLower();
            return name;
        }

        _conditionsMob[959].Add(GetNpcName(10461), 10461); // xx之物
        _conditionsMob[959].Add(GetNpcName(10462), 10462);
        _conditionsMob[959].Add(GetNpcName(10463), 10463);

        _conditionsMob[957].Add(GetNpcName(10697), 10697); // 毕舍遮
        _conditionsMob[957].Add(GetNpcName(10698), 10698); // 金刚尾
        _conditionsMob[957].Add(GetNpcName(10701), 10701); // 阿输陀花

        _conditionsMob[817].Add(GetNpcName(8789), 8789); // 破裂的隆卡器皿
        _conditionsMob[817].Add(GetNpcName(8598), 8598); // 破裂的隆卡人偶
        _conditionsMob[817].Add(GetNpcName(8599), 8599); // 破裂的隆卡石蒺藜

        _conditionsMob[613].Add(GetNpcName(5750), 5750); // 观梦螺
        _conditionsMob[613].Add(GetNpcName(5751), 5751); // 无壳观梦螺

        _conditionsMob[612].Add(GetNpcName(5685), 5685); // 狄亚卡
        _conditionsMob[612].Add(GetNpcName(5671), 5671); // 莱西

        _conditionsMob[402].Add(GetNpcName(3556), 3556); // 美拉西迪亚薇薇尔飞龙
        _conditionsMob[402].Add(GetNpcName(3580), 3580); // 小海德拉
        _conditionsMob[402].Add(GetNpcName(3540), 3540); // 亚拉戈奇美拉

        _conditionsMob[147].Add(GetNpcName(113), 113); // 土元精

        // gather
        _conditionsMob[814].Add(GetItemName(27759), 27759); // 矮人棉
        _conditionsMob[400].Add(GetItemName(12634), 12634); // 星极花
        _conditionsMob[400].Add(GetItemName(12536), 12536); // 皇金矿

        // discard
        _conditionsMob[961].Add(GetItemName(36256), 36256);
        _conditionsMob[813].Add(GetItemName(27850), 27850);
    }

    private delegate void ProcessSpawnNpcDelegate(nint a1, uint a2, nint packetData);
}