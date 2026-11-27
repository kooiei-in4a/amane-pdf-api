using System.Diagnostics;

namespace Amane.Pdf.Api.Tests;

internal static class TestExecutable
{
    // テストは並列に実行され、別のテストが同時にProcess.Startでforkします。
    // このプロセスが実行ファイルを書き込み用に開いている間にforkが起きると、子プロセスが
    // exec するまでその fd を持ち続けます。その間にこのファイルを exec すると ETXTBSY
    // (Text file busy) で失敗します。これを避けるため、内容は別名の一時ファイルに書き、
    // 実行ファイルの作成は子プロセスの install に任せます。こうすると、実行ファイルへの
    // 書き込み用 fd をこのプロセスが一度も持ちません。
    public static string Write(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return path;
        }

        var source = path + "." + Guid.NewGuid().ToString("N") + ".source";
        try
        {
            File.WriteAllText(source, content);
            using var install = Process.Start(new ProcessStartInfo("/usr/bin/install", ["-m", "700", "--", source, path])
            {
                UseShellExecute = false,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("install を起動できませんでした。");
            var error = install.StandardError.ReadToEnd();
            install.WaitForExit();
            if (install.ExitCode != 0)
                throw new InvalidOperationException($"install が終了コード {install.ExitCode} で失敗しました: {error}");
        }
        finally
        {
            File.Delete(source);
        }

        return path;
    }
}
