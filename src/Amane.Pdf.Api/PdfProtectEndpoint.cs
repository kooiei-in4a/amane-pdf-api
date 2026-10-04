using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.Text;

namespace Amane.Pdf.Api;

public static class PdfProtectEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        try
        {
            if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
                !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                await Results.Problem(statusCode: 400, title: "multipart/form-data が必要です。").ExecuteAsync(context);
                return;
            }
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (IOException)
            {
                throw new BadHttpRequestException("Invalid multipart body.");
            }
            var file = form.Files.GetFile("file");
            var password = form["password"].ToString();
            if (file is null || form.Files.Count != 1 || form["password"].Count != 1 || string.IsNullOrEmpty(password))
            {
                await Results.Problem(statusCode: 400, title: "file と password が必要です。").ExecuteAsync(context);
                return;
            }
            if (Encoding.UTF8.GetByteCount(password) > 127 || password.Any(char.IsControl))
            {
                await Results.Problem(statusCode: 400, title: "password は制御文字を含まないUTF-8で127 bytes以内にしてください。").ExecuteAsync(context);
                return;
            }
            using var files = new TemporaryPdfFiles(options.Value.TempRoot);
            await using (var input = TemporaryPdfFiles.CreatePrivateFile(files.InputPath))
            {
                await file.CopyToAsync(input, context.RequestAborted);
            }
            await processor.ValidateAsync(files, context.RequestAborted);
            await processor.ProtectAsync(files, password, context.RequestAborted);
            await using var output = File.OpenRead(files.OutputPath);
            context.Response.ContentType = "application/pdf";
            context.Response.ContentLength = output.Length;
            context.Response.Headers.ContentDisposition = "attachment; filename=protected.pdf";
            await output.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (PdfInputException)
        {
            await Results.Problem(statusCode: 422, title: "未暗号化の正常なPDFが必要です。").ExecuteAsync(context);
        }
        catch (InvalidDataException)
        {
            await Results.Problem(statusCode: 400, title: "multipart/form-data の形式が不正です。").ExecuteAsync(context);
        }
        catch (BadHttpRequestException)
        {
            await Results.Problem(statusCode: 400, title: "リクエストの形式が不正です。").ExecuteAsync(context);
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
