using Microsoft.AspNetCore.Http.Features;

namespace Amane.Pdf.Api;

internal static class PdfEndpointHelpers
{
    public static async Task ExecuteAsync(
        HttpContext context,
        PdfOptions options,
        string downloadFileName,
        Action? validateRequest,
        Func<TemporaryPdfFiles, CancellationToken, Task> readUploadAsync,
        Func<TemporaryPdfFiles, CancellationToken, Task> processAsync,
        long? maxRequestBytes = null)
    {
        var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        try
        {
            validateRequest?.Invoke();
            ApplyRequestSizeLimit(context, maxRequestBytes ?? options.MaxRequestBytes);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            using var files = new TemporaryPdfFiles(options.TempRoot);
            await readUploadAsync(files, operation.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.QpdfTimeoutSeconds));
            try
            {
                await processAsync(files, timeout.Token);
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
            await SendPdfAsync(context, files.OutputPath, downloadFileName, operation.Token);
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
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("PdfEndpoint").LogError("PDF処理で内部障害が発生しました。");
            context.Response.Clear();
            await Results.Problem(statusCode: 500, title: "PDF処理に失敗しました。").ExecuteAsync(context);
        }
    }

    private static void ApplyRequestSizeLimit(HttpContext context, long maxRequestBytes)
    {
        if (context.Request.ContentLength > maxRequestBytes)
        {
            throw new BadHttpRequestException("Request size limit exceeded.", 413);
        }

        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = maxRequestBytes;
    }

    private static async Task SendPdfAsync(HttpContext context, string path, string fileName, CancellationToken cancellationToken)
    {
        await using var output = File.OpenRead(path);
        context.Response.ContentType = "application/pdf";
        context.Response.ContentLength = output.Length;
        context.Response.Headers.ContentDisposition = $"attachment; filename={fileName}";
        await output.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
