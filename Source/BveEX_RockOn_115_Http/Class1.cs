using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BveTypes.ClassWrappers;
using BveEx.Extensions.Native;
using BveEx.Extensions.Native.Input;
using BveEx.PluginHost.Plugins;
using BveEx.PluginHost.Plugins.Extensions;
using BveEx.PluginHost;
using BveEx.PluginHost.Input;
using System.Diagnostics;
using System.Threading;
using System.Reflection;

namespace BveEX_RockOn_115_Http
{
    [Plugin(PluginType.VehiclePlugin)]
    public class PluginMain : AssemblyPluginBase
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        // ======================
        // ▼ HTTP エンドポイント定義
        // ======================

        // Publish 相当: 車両状態をまとめて POST する送信先
        private const string PublishUrl = "http://localhost:5000/bve/snapshot";

        // Subscribe 相当: ハンドル操作コマンドを Tick ごとに GET で取得する取得元
        private const string CommandsUrl = "http://localhost:5000/bve/commands";

        // 既存サーバーの生存確認(TCP接続チェック)用
        private const string ServerHost = "127.0.0.1";
        private const int ServerPort = 5000;

        // ======================
        // ▼ ログ出力設定
        // ======================
        // プラグインDLLと同じフォルダを基準にした相対パス。
        // 例: BveEx\Plugins\xxx\Log\ 以下にログファイルが作成される。
        private const string RelativeLogDirectory = "Log";

        // panelArray のインデックス（意味を持たせて可読性向上）
        private const int PanelIndexCount = 9; // panelArray[0]～[8] を使用

        // ======================
        // ▼ HTTPブリッジサーバー自動起動設定
        // ======================
        // 車両データフォルダ（プラグインDLLと同じフォルダ）に同梱した Python スクリプト。
        private const string ServerScriptFileName = "bve_http_server.py";

        // タイムアウトを3000msにしている理由:
        // このプロセスからの「最初の1回」のHTTPリクエストだけ、.NET側のネットワークスタック
        // 初期化(プロキシ自動検出やソケット初期化など)により約2秒かかることを実測で確認した。
        // 500ms/1000msのように短いタイムアウトだと最初のリクエストが必ずキャンセルされ、
        // かつキャンセルされた接続はそのまま初期化を終えられないため、次回以降も同じ約2秒が
        // 繰り返され「毎回失敗し続ける」状態になっていた。3000msあれば最初の1回を乗り切れ、
        // 以降のリクエストは(実測で)ほぼ0msで返るため、通信の実害はない。
        private static readonly HttpClient httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(3000)
        };

        private static readonly object ServerStartLock = new object();
        private static Process serverProcess;

        private readonly string logFilePath;

        // GET /bve/commands のレスポンス例: {"reverser":1,"power":2,"brake":0}
        private static readonly Regex CommandFieldRegex = new Regex(
            "\"(reverser|power|brake)\"\\s*:\\s*(-?\\d+)",
            RegexOptions.Compiled);

        static PluginMain()
        {
            AllocConsole();
            httpClient.DefaultRequestHeaders.ConnectionClose = true;
            // .NET Framework の既定では同一ホストへの同時接続数が小さく制限されており、
            // 60Hz近い頻度でリクエストを投げると詰まりやすい。念のため引き上げておく。
            System.Net.ServicePointManager.DefaultConnectionLimit = 50;
        }

        public PluginMain(PluginBuilder builder) : base(builder)
        {
            string logDirectory = ResolveLogDirectory();
            Directory.CreateDirectory(logDirectory);

            string logFileTime = DateTime.Now.ToString("yyyyMMddHHmmss");
            logFilePath = Path.Combine(logDirectory, $"{logFileTime}.csv");

            StartHttpServerIfNeeded();
        }

        /// <summary>
        /// プラグインDLLと同じフォルダを基準に、相対パスで指定したログフォルダの
        /// フルパスを解決する（フォルダが無い場合は Tick 開始前に自動作成する）。
        /// </summary>
        private static string ResolveLogDirectory()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            return Path.Combine(assemblyDir, RelativeLogDirectory);
        }

        /// <summary>
        /// シナリオ起動時に、車両データフォルダ同梱の Python ブリッジサーバーを自動起動する。
        /// 既に（このプラグイン自身、または外部プロセスとして）起動済みなら何もしない。
        /// </summary>
        private static void StartHttpServerIfNeeded()
        {
            lock (ServerStartLock)
            {
                if (serverProcess != null && !serverProcess.HasExited)
                    return;

                if (IsServerAlreadyRunning())
                {
                    Console.WriteLine("[INFO] HTTP bridge server is already running (external).");
                    return;
                }

                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string scriptPath = Path.Combine(assemblyDir, ServerScriptFileName);

                if (!File.Exists(scriptPath))
                {
                    Console.WriteLine($"[ERROR] HTTP bridge server script not found: {scriptPath}");
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"-u \"{scriptPath}\"",
                    WorkingDirectory = assemblyDir,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };

                try
                {
                    serverProcess = Process.Start(psi);
                    Console.WriteLine("[INFO] HTTP bridge server started.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to start HTTP bridge server: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// localhost:5000 へ生TCP接続を試み、既にサーバー(リスナー)が存在するかを確認する。
        /// HttpClient(async)ではなくTcpClientの同期APIを使う理由: BVE本体のスレッドには
        /// SynchronizationContextが存在し、"GetAwaiter().GetResult()"でawaitの結果を
        /// 同期的に取り出すと、その継続処理が同じスレッドに戻れず永久に完了しない
        /// （デッドロック/実質的に常にタイムアウトしてfalseを返す）ため、
        /// ここではSynchronizationContextに依存しないTcpClientの同期接続を使う。
        /// </summary>
        private static bool IsServerAlreadyRunning()
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult result = client.BeginConnect(ServerHost, ServerPort, null, null);
                    bool connected = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
                    if (connected && client.Connected)
                    {
                        client.EndConnect(result);
                        return true;
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        public override void Dispose()
        {
            lock (ServerStartLock)
            {
                try
                {
                    if (serverProcess != null && !serverProcess.HasExited)
                    {
                        serverProcess.Kill();
                        Console.WriteLine("[INFO] HTTP bridge server stopped.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to stop HTTP bridge server: {ex.Message}");
                }
                finally
                {
                    serverProcess = null;
                }
            }
        }

        // Tick は約60Hzで呼ばれるが、HTTPの往復は1回あたり数msでは終わらないことがある。
        // 前回の呼び出しが終わる前に次のTickが新しいリクエストを重ねて投げてしまうと、
        // 際限なく未完了のリクエストが積み重なり、結果として「タイムアウト待ちの列に
        // 並んだまま時間切れになる」状態が延々と続く（=毎回失敗しているように見える）。
        // そのため、前回分が完了するまでは今回のTickでは投げずにスキップする。
        private int snapshotInFlight = 0;
        private int commandsInFlight = 0;

        public override void Tick(TimeSpan elapsed)
        {
            VehicleSnapshot snapshot = CollectVehicleSnapshot();

            WriteLog(snapshot);

            if (Interlocked.CompareExchange(ref snapshotInFlight, 1, 0) == 0)
            {
                _ = RunPublishSnapshotAsync(snapshot);
            }

            if (Interlocked.CompareExchange(ref commandsInFlight, 1, 0) == 0)
            {
                _ = RunFetchAndApplyCommandsAsync();
            }
        }

        private async Task RunPublishSnapshotAsync(VehicleSnapshot s)
        {
            try
            {
                await PublishSnapshotAsync(s);
            }
            finally
            {
                Interlocked.Exchange(ref snapshotInFlight, 0);
            }
        }

        private async Task RunFetchAndApplyCommandsAsync()
        {
            try
            {
                await FetchAndApplyCommandsAsync();
            }
            finally
            {
                Interlocked.Exchange(ref commandsInFlight, 0);
            }
        }

        /// <summary>
        /// 現在フレームの車両状態を BVE から取得し、まとめて返す。
        /// </summary>
        private VehicleSnapshot CollectVehicleSnapshot()
        {
            var instruments = BveHacker.Scenario.Vehicle.Instruments;
            HandleSet handles = instruments.AtsPlugin.Handles;

            return new VehicleSnapshot
            {
                Time = BveHacker.Scenario.TimeManager.TimeMilliseconds,
                Location = BveHacker.Scenario.VehicleLocation.Location,
                Speed = BveHacker.Scenario.VehicleLocation.Speed * 3.6,

                Power = handles.PowerNotch,
                Brake = handles.BrakeNotch,
                Reverser = handles.ReverserPosition,
                ConstantSpeed = handles.ConstantSpeedMode,

                Pilot = instruments.AtsPlugin.Doors.AreAllClosed,
                Ampere = instruments.Electricity.MotorState.Current,
                PowerStepIndex = instruments.Electricity.Performance.Power.CurrentStepIndex,
                BrakeCylinderPressure = instruments.BrakeSystem.Ecb.OutputPressure.Value,

                PanelArray = instruments.AtsPlugin.PanelArray,
                SoundArray = instruments.AtsPlugin.SoundArray
            };
        }

        /// <summary>
        /// 車両状態を CSV 形式でログファイルに追記する。
        /// </summary>
        private void WriteLog(VehicleSnapshot s)
        {
            using (var sw = new StreamWriter(logFilePath, append: true, Encoding.GetEncoding("shift_jis")))
            {
                string panelValues = string.Join(",", s.PanelArray.Take(PanelIndexCount));

                sw.Write(
                    $"{s.Time},{s.Location},{s.Speed},{s.Reverser},{s.Power},{s.Brake},{s.ConstantSpeed},{s.Pilot},{s.Ampere},{s.BrakeCylinderPressure}," +
                    $"{panelValues}\n");
            }
        }

        /// <summary>
        /// 車両状態を1つのJSONにまとめてHTTP POSTする（MQTT Publishの置き換え）。
        /// </summary>
        private async Task PublishSnapshotAsync(VehicleSnapshot s)
        {
            string panelJson = "[" + string.Join(",", s.PanelArray.Take(PanelIndexCount)) + "]";
            string soundJson = "[" + string.Join(",", s.SoundArray[0], s.SoundArray[1], s.SoundArray[3], s.SoundArray[4]) + "]";

            string json =
                "{" +
                $"\"time\":{s.Time}," +
                $"\"speed\":{s.Speed.ToString("F2")}," +
                $"\"location\":{s.Location.ToString("F1")}," +
                $"\"pilot\":{(s.Pilot ? "1" : "0")}," +
                $"\"am\":{s.Ampere.ToString("F1")}," +
                $"\"panel\":{panelJson}," +
                $"\"sound\":{soundJson}" +
                "}";

            try
            {
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                {
                    await httpClient.PostAsync(PublishUrl, content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to POST snapshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Tick ごとに GET でハンドル操作コマンドを取得し、対応するハンドルへ反映する
        /// （MQTT Subscribe の置き換え）。
        /// レスポンス例: {"reverser":1,"power":2,"brake":0}
        /// </summary>
        private async Task FetchAndApplyCommandsAsync()
        {
            string body;
            try
            {
                body = await httpClient.GetStringAsync(CommandsUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to GET commands: {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(body))
                return;

            var handles = BveHacker.Scenario.Vehicle.Instruments.AtsPlugin.Handles;

            foreach (Match match in CommandFieldRegex.Matches(body))
            {
                string field = match.Groups[1].Value;
                int val = int.Parse(match.Groups[2].Value);

                switch (field)
                {
                    case "reverser":
                        handles.ReverserPosition = (ReverserPosition)val;
                        break;
                    case "power":
                        handles.PowerNotch = val;
                        break;
                    case "brake":
                        handles.BrakeNotch = val;
                        break;
                }
            }
        }

        /// <summary>
        /// 1フレーム分の車両状態をまとめて保持するデータ構造。
        /// </summary>
        private struct VehicleSnapshot
        {
            public int Time;
            public double Location;
            public double Speed;

            public int Power;
            public int Brake;
            public ReverserPosition Reverser;
            public ConstantSpeedMode ConstantSpeed;

            public bool Pilot;
            public double Ampere;
            public int PowerStepIndex;
            public double BrakeCylinderPressure;

            public int[] PanelArray;
            public int[] SoundArray;
        }
    }
}
