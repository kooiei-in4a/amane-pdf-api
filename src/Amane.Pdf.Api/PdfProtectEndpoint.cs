using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;

namespace Amane.Pdf.Api;

public static class PdfProtectEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        try
        {
            if (context.Request.ContentLength > options.Value.MaxRequestBytes)
            {
                throw new BadHttpRequestException("Request size limit exceeded.", 413);
            }
            var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = options.Value.MaxRequestBytes;
            using var files = new TemporaryPdfFiles(options.Value.TempRoot);
            var password = await MultipartPdfUpload.ReadAsync(context.Request, files, options.Value, context.RequestAborted);
            var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.QpdfTimeoutSeconds));
            try
            {
                await processor.ValidateAsync(files, timeout.Token);
                await processor.ProtectAsync(files, password, timeout.Token);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                await Results.Problem(statusCode: 504, title: "PDF処理が制限時間を超過しました。").ExecuteAsync(context);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                context.Abort();
                return;
            }
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
        catch (BadHttpRequestException exception)
        {
            var tooLarge = exception.StatusCode == 413;
            await Results.Problem(statusCode: tooLarge ? 413 : 400,
                title: tooLarge ? "PDFまたはリクエストのサイズ上限を超過しました。" : "リクエストの形式が不正です。").ExecuteAsync(context);
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
            context.Response.Clear();
            await Results.Problem(statusCode: 500, title: "PDF処理に失敗しました。").ExecuteAsync(context);
        }
    }
}
