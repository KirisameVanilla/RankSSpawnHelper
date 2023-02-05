﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using RankSSpawnHelper.Attributes;
using RankSSpawnHelper.Models;
using RankSSpawnHelper.Ui;

namespace RankSSpawnHelper
{
    public class EntryPoint : IDalamudPlugin
    {
        private readonly Assembly _assembly;
        private readonly PluginCommandManager<EntryPoint> _commandManager;
        private readonly AssemblyLoadContext _context;
        private readonly WindowSystem _windowSystem;

        public EntryPoint([RequiredVersion("1.0")] DalamudPluginInterface pi)
        {
            pi.Create<DalamudApi>();
            pi.Create<Plugin>();
            _assembly = Assembly.GetExecutingAssembly();
            _context  = AssemblyLoadContext.GetLoadContext(_assembly);
            DalamudApi.Interface.Inject(this, Array.Empty<object>());

            LoadCosturaAssembles();

            // Get or create a configuration object
            Plugin.Configuration = (Configuration)pi.GetPluginConfig() ?? pi.Create<Configuration>();

            Plugin.Features = new Features.Features();
            Plugin.Managers = new Managers.Managers();

            // Initialize the UI
            _windowSystem  = new WindowSystem(typeof(EntryPoint).AssemblyQualifiedName);
            Plugin.Windows = new Windows(ref _windowSystem);

            DalamudApi.Interface.UiBuilder.Draw         += _windowSystem.Draw;
            DalamudApi.Interface.UiBuilder.OpenConfigUi += OpenConfigUi;

            // Load all of our commands
            _commandManager = new PluginCommandManager<EntryPoint>(this);

            var pluginVersion = _assembly.GetName().Version.ToString();

#if RELEASE
            if (Plugin.Configuration.PluginVersion == pluginVersion) 
                return;
            Plugin.Configuration.PluginVersion = pluginVersion;
#endif

            Plugin.Print(new List<Payload>
                         {
                             new TextPayload($"版本 {pluginVersion} 的更新日志:\n"),
                             new UIForegroundPayload(35),
                             new TextPayload("  [-] 修复了在成功触发炫学后不会向服务器发送触发成功的BUG\n"),
                             new TextPayload("  [-] 修复了接收触发消息总开关无效的BUG\n"),
                             new TextPayload("  [+] 现在在副本里不会接受触发消息了\n"),
                             new TextPayload("  [+] 新增玩家搜索计数功能\n"),
                             new TextPayload("      注: 1.计数不是100%准确. 如果在计数里的玩家离开地图后不会从计数里移除\n"),
                             new TextPayload("           2.计数会在自己过图后清除并且只能从游戏里的 玩家搜索 这个功能增加"),
                         });
        }

        public string Name => "SpawnHelper";

        private void LoadCosturaAssembles()
        {
            Span<byte> span = new byte[65536];
            foreach (var text in from name in _assembly.GetManifestResourceNames()
                                 where name.StartsWith("costura.") && name.EndsWith(".dll.compressed")
                                 select name)
            {
                using var deflateStream = new DeflateStream(_assembly.GetManifestResourceStream(text), CompressionMode.Decompress, false);
                using var memoryStream  = new MemoryStream();

                int num;
                while ((num = deflateStream.Read(span)) != 0)
                {
                    Stream stream = memoryStream;
                    var    span2  = span;
                    stream.Write(span2[..num]);
                }

                memoryStream.Position = 0L;
                _context.LoadFromStream(memoryStream);
            }
        }

#region Commands initialization
        [Command("/shelper")]
        [HelpMessage("打开或关闭设置菜单")]
        public void ToggleConfigUi(string command, string args)
        {
            Plugin.Windows.PluginWindow.Toggle();
        }

        private void OpenConfigUi()
        {
            Plugin.Windows.PluginWindow.IsOpen = true;
        }

        [Command("/clr")]
        [HelpMessage("清除计数器")]
        public void ClearTracker_1(string cmd, string args)
        {
            ClearTracker_Internal(cmd, args);
        }

        [Command("/清除计数")]
        [HelpMessage("清除计数器")]
        public void ClearTracker_2(string cmd, string args)
        {
            ClearTracker_Internal(cmd, args);
        }

        private static void ClearTracker_Internal(string cmd, string args)
        {
            switch (args)
            {
                case "全部":
                case "所有":
                case "all":
                {
                    Plugin.Features.Counter.RemoveInstance();
                    Plugin.Print("已清除所有计数");
                    break;
                }
                case "当前":
                case "cur":
                case "current":
                {
                    var currentInstance = Plugin.Managers.Data.Player.GetCurrentTerritory();
                    Plugin.Features.Counter.RemoveInstance(currentInstance);
                    Plugin.Print("已清除当前区域的计数");
                    break;
                }
                default:
                {
                    Plugin.Print(new List<Payload>
                                 {
                                     new UIForegroundPayload(518),
                                     new TextPayload($"使用方法: {cmd} [cur/all]. 比如清除当前计数: {cmd} cur"),
                                     new UIForegroundPayload(0)
                                 });
                    return;
                }
            }
        }

        [Command("/ggnore")]
        [HelpMessage("触发失败,寄了")]
        public void AttempFailed_1(string cmd, string args)
        {
            AttempFailed_Internal(cmd, args);
        }

        [Command("/寄了")]
        [HelpMessage("触发失败,寄了")]
        public void AttempFailed_2(string cmd, string args)
        {
            AttempFailed_Internal(cmd, args);
        }

        private static void AttempFailed_Internal(string cmd, string args)
        {
            if (!Plugin.Managers.Socket.Connected())
            {
                var currentInstance = Plugin.Managers.Data.Player.GetCurrentTerritory();
                if (!Plugin.Features.Counter.GetLocalTrackers().TryGetValue(currentInstance, out var tracker))
                    return;

                var startTime = DateTimeOffset.FromUnixTimeSeconds(tracker.startTime).LocalDateTime;
                var endTime   = DateTimeOffset.Now.LocalDateTime;

                var message = $"{currentInstance}的计数寄了！\n" +
                              $"开始时间: {startTime.ToShortDateString()}/{startTime.ToShortTimeString()}\n" +
                              $"结束时间: {endTime.ToShortDateString()}/{endTime.ToShortTimeString()}\n" +
                              "计数详情: ";

                foreach (var (k, v) in tracker.counter)
                {
                    message += $"    {k}: {v}\n";
                }

                Plugin.Print(new List<Payload>
                             {
                                 new UIForegroundPayload(518),
                                 new TextPayload(message + "PS:消息已复制到剪贴板"),
                                 new UIForegroundPayload(0)
                             });

                ImGui.SetClipboardText(message);
                return;
            }

            Plugin.Managers.Socket.SendMessage(new NetMessage
                                               {
                                                   Type        = "ggnore",
                                                   Instance    = Plugin.Managers.Data.Player.GetCurrentTerritory(),
                                                   TerritoryId = DalamudApi.ClientState.TerritoryType,
                                                   Failed      = true
                                               });
        }
#endregion

#region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            _commandManager.Dispose();

            Plugin.Managers.Data.MapTexture.Dispose();
            Plugin.Configuration.Save();
            Plugin.Managers.Dispose();
            Plugin.Features.Dispose();

            DalamudApi.Interface.UiBuilder.Draw -= _windowSystem.Draw;
            _windowSystem.RemoveAllWindows();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
#endregion
    }
}