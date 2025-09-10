using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ClientPlugin.GUI;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Plugins;

namespace ClientPlugin
{
    public class Plugin : IPlugin, ICommonPlugin
    {
        public const string Name = "CosmicWineFixes";
        public static Plugin Instance { get; private set; }

        public long Tick { get; private set; }

        public IPluginLogger Log => Logger;
        private static readonly IPluginLogger Logger = new PluginLogger(Name);

        public IPluginConfig Config => config?.Data;
        private PersistentConfig<PluginConfig> config;
        private static readonly string ConfigFileName = $"{Name}.cfg";

        private static readonly object InitializationMutex = new object();
        private static bool initialized;
        private static bool failed;

        private bool stopThread = false;
        private Thread staThread;
        private Queue<Action> threadActionQueue = new Queue<Action>();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Init(object gameInstance)
        {
            Instance = this;

            Log.Info("Loading");

            var configPath = Path.Combine(MyFileSystem.UserDataPath, "Storage", ConfigFileName);
            config = PersistentConfig<PluginConfig>.Load(Log, configPath);

            Common.SetPlugin(this);

            if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            {
                failed = true;
                return;
            }

            Log.Debug("Successfully loaded");
        }

        public void Dispose()
        {
            try
            {
                Log.Debug("Stopping STAThread");
                stopThread = true;
                staThread.Join();
                Log.Debug("Stopped STAThread");
            }
            catch (Exception ex)
            {
                Log.Critical(ex, "Dispose failed");
            }

            Instance = null;
        }

        public void Update()
        {
            EnsureInitialized();
            try
            {
                if (!failed)
                {
                    CustomUpdate();
                    Tick++;
                }
            }
            catch (Exception ex)
            {
                Log.Critical(ex, "Update failed");
                failed = true;
            }
        }

        private void EnsureInitialized()
        {
            lock (InitializationMutex)
            {
                if (initialized || failed)
                    return;

                Log.Info("Initializing");
                try
                {
                    Initialize();
                }
                catch (Exception ex)
                {
                    Log.Critical(ex, "Failed to initialize plugin");
                    failed = true;
                    return;
                }

                Log.Debug("Successfully initialized");
                initialized = true;
            }
        }

        private void Initialize()
        {
            Log.Debug("Starting STAThread");
            staThread = new Thread(() =>
            {
                while (!stopThread)
                {
                    while (threadActionQueue.Count > 0)
                    {
                        Log.Debug("Executing action on STAThread");
                        Action a = threadActionQueue.Dequeue();
                        a?.Invoke();
                    }
                    Thread.Sleep(Config.ThreadExecutionIntervalMs);
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
        }

        private void CustomUpdate()
        {
            // Nothing to do in every tick
        }

        public void ExecuteActionOnStaThread(Action action)
        {
            threadActionQueue.Enqueue(action);
        }

        public void OpenConfigDialog()
        {
            MyGuiSandbox.AddScreen(new MyPluginConfigDialog());
        }

        // -------------------------------
        // Clipboard compatibility helpers
        // -------------------------------
        public static void SetClipboardText(string text)
        {
            if (IsWayland())
            {
                WaylandClipboard.SetClipboardText(text);
            }
            else
            {
                ExecuteOnStaThreadSafe(() =>
                {
                    try
                    {
                        System.Windows.Forms.Clipboard.SetText(text);
                    }
                    catch (Exception ex)
                    {
                        Instance?.Log?.Critical(ex, "Failed to set clipboard (X11/XWayland)");
                    }
                });
            }
        }

        public static string GetClipboardText()
        {
            if (IsWayland())
            {
                return WaylandClipboard.GetClipboardText();
            }

            string result = string.Empty;
            ExecuteOnStaThreadSafe(() =>
            {
                try
                {
                    result = System.Windows.Forms.Clipboard.GetText();
                }
                catch (Exception ex)
                {
                    Instance?.Log?.Critical(ex, "Failed to get clipboard (X11/XWayland)");
                }
            });
            return result;
        }

        private static bool IsWayland()
        {
            return Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.ToLower() == "wayland";
        }

        private static void ExecuteOnStaThreadSafe(Action action)
        {
            if (Instance == null)
                return;

            Instance.ExecuteActionOnStaThread(action);
        }
    }

    internal static class WaylandClipboard
    {
        public static void SetClipboardText(string text)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wl-copy",
                    RedirectStandardInput = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.StandardInput.Write(text);
                    proc.StandardInput.Close();
                    proc.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Log?.Critical(ex, "Failed to set clipboard (wl-copy)");
            }
        }

        public static string GetClipboardText()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wl-paste",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    return output;
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Log?.Critical(ex, "Failed to get clipboard (wl-paste)");
            }
            return string.Empty;
        }
    }
}
