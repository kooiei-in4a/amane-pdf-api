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
        long? maxRequestBytes = null,
        Action<HttpResponse>? onSend = null,
        string contentType = "application/pdf")
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
            await SendFileAsync(context, files.OutputPath, downloadFileName, operation.Token, onSend, contentType);
        }
        catch (PdfUnlockException exception)
        {
            var (reason, title) = exception.Reason switch
            {
                PdfUnlockReason.NotEncrypted => ("not-encrypted", "パスワードが設定されたPDFが必要です。"),
                PdfUnlockReason.NoOpenPassword => ("no-open-password", "開くためのパスワードが設定されていないPDFは解除できません。"),
                PdfUnlockReason.WrongPassword => ("wrong-password", "パスワードが正しくありません。"),
                PdfUnlockReason.InvalidPdf => ("invalid-pdf", "正常なPDFが必要です。"),
                // All producers use the four named values; no request data is converted to this enum.
                // This unreachable guard bypasses the sibling catch (Exception); update the switch when adding a reason.
                _ => throw new InvalidOperationException("Unknown PDF unlock reason.")
            };
            await Results.Problem(statusCode: 422, title: title,
                extensions: new Dictionary<string, object?> { ["reason"] = reason }).ExecuteAsync(context);
        }
        catch (PdfSplitOutputTooLargeException)
        {
            await Results.Problem(statusCode: 422, title: PdfSplitOutputTooLargeException.Title,
                extensions: new Dictionary<string, object?> { ["reason"] = "output-too-large" }).ExecuteAsync(context);
        }
        catch (PdfCleanTooComplexException)
        {
            await Results.Problem(statusCode: 422, title: "このPDFは情報除去の処理上限を超えています。",
                extensions: new Dictionary<string, object?> { ["reason"] = "too-complex" }).ExecuteAsync(context);
        }
        catch (PdfOverlayInputException exception)
        {
            var complex = exception.Reason == PdfOverlayReason.TooComplex;
            await Results.Problem(statusCode: 422,
                title: complex ? "このPDFは描画の処理上限を超えています。" : "このPDFのページ属性は描画に対応していません。",
                extensions: new Dictionary<string, object?> { ["reason"] = complex ? "too-complex" : "unsupported-pdf" }).ExecuteAsync(context);
        }
        catch (PdfInputException)
        {
            await Results.Problem(statusCode: 422, title: "このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。").ExecuteAsync(context);
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

    private static async Task SendFileAsync(HttpContext context, string path, string fileName, CancellationToken cancellationToken,
        Action<HttpResponse>? onSend, string contentType)
    {
        await using var output = File.OpenRead(path);
        cancellationToken.ThrowIfCancellationRequested();
        context.Response.ContentType = contentType;
        context.Response.ContentLength = output.Length;
        context.Response.Headers.ContentDisposition = $"attachment; filename={fileName}";
        onSend?.Invoke(context.Response);
        await output.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
