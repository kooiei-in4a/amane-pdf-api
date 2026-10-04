using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfProtectEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        try
        {
            if (!context.Request.HasFormContentType)
            {
                await Results.Problem(statusCode: 400, title: "multipart/form-data が必要です。").ExecuteAsync(context);
                return;
            }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = form.Files.GetFile("file");
            var password = form["password"].ToString();
            if (file is null || string.IsNullOrEmpty(password))
            {
                await Results.Problem(statusCode: 400, title: "file と password が必要です。").ExecuteAsync(context);
                return;
            }
            using var files = new TemporaryPdfFiles(options.Value.TempRoot);
            await using (var input = TemporaryPdfFiles.CreatePrivateFile(files.InputPath))
            {
                await file.CopyToAsync(input, context.RequestAborted);
            }
            await processor.ProtectAsync(files, password, context.RequestAborted);
            await using var output = File.OpenRead(files.OutputPath);
            context.Response.ContentType = "application/pdf";
            context.Response.ContentLength = output.Length;
            context.Response.Headers.ContentDisposition = "attachment; filename=protected.pdf";
            await output.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The process has exited and temporary files have been removed before returning.
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("PdfProtect").LogError("PDF処理で内部障害が発生しました。");
            await Results.Problem(statusCode: 500, title: "PDF処理に失敗しました。").ExecuteAsync(context);
        }
    }
}
