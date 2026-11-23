using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Forms;

namespace DeskTodo;

internal sealed class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }

    // 纯界面动效状态，不存盘
    [System.Text.Json.Serialization.JsonIgnore]
    public float Strike;

    [System.Text.Json.Serialization.JsonIgnore]
    public float Wipe;
}

internal sealed class AppData
{
    public List<TodoItem> Items { get; set; } = new();
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? W { get; set; }
    public int? H { get; set; }
}

internal static class Build
{
    // 调试版：不抓屏、不排除抓屏，只画一块实心底，方便远程看界面
    public static readonly bool DebugUi =
#if TDL_DEBUGUI
        true;
#else
        false;
#endif
}

internal static class Store
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Build.DebugUi ? "桌面清单-调试" : "桌面清单");

    public static readonly string FilePath = Path.Combine(Dir, "todo.json");

    public static AppData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppData? data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath));
                if (data != null)
                {
                    data.Items ??= new List<TodoItem>();
                    return data;
                }
            }
        }
        catch
        {
            // 数据文件损坏时按空清单启动，不打扰用户
        }

        return new AppData();
    }

    public static void Save(AppData data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, options));
        }
        catch
        {
            // 写盘失败不影响使用
        }
    }
}

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int appMode);

    [STAThread]
    private static void Main()
    {
        string mutexName = Build.DebugUi
            ? @"Local\DesktopTodoList_桌面清单_调试"
            : @"Local\DesktopTodoList_桌面清单";
        using var mutex = new Mutex(true, mutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("清单已经在运行了。", "桌面清单", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 崩了别弹窗挡路，写条日志就好，窗口还能接着用
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog(e.ExceptionObject as Exception);

        try
        {
            // 让系统自带控件（输入框、菜单）走深色主题
            _ = SetPreferredAppMode(2);
        }
        catch
        {
            // 老系统没有这个入口就跳过
        }

        FixAutostartPath();

        Application.Run(new MainForm());
    }

    // 开机自启写的是绝对路径，程序被挪了位置就会失效。
    // 只在启动项已存在时把它改成当前路径：用户在任务管理器里关掉自启，这里不会又给加回去。
    private static void FixAutostartPath()
    {
        if (Build.DebugUi)
        {
            return;
        }

        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "桌面清单";

        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            string want = "\"" + exe + "\"";
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null)
            {
                return;
            }

            if (key.GetValue(valueName) is string current
                && !string.Equals(current, want, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(valueName, want, Microsoft.Win32.RegistryValueKind.String);
            }
        }
        catch
        {
            // 注册表动不了就算了，不影响正常使用
        }
    }

    private static void CrashLog(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            File.AppendAllText(Path.Combine(Store.Dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不了也没别的办法
        }
    }
}
