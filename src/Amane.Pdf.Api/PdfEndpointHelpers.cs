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
        string contentType = "application/pdf",
        bool images = false,
        string? tooLargeTitle = null)
    {
        var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var diagnostics = new PdfProcessingDiagnostics(context);
        try
        {
            using (var stage = PdfProcessingDiagnostics.Measure(PdfStage.RequestValidation))
            {
                validateRequest?.Invoke();
                ApplyRequestSizeLimit(context, maxRequestBytes ?? options.MaxRequestBytes);
                stage.Complete();
            }
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            using var files = new TemporaryPdfFiles(options.TempRoot);
            using (var stage = PdfProcessingDiagnostics.Measure(PdfStage.Upload))
            {
                await readUploadAsync(files, operation.Token);
                stage.Complete();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.QpdfTimeoutSeconds));
            try
            {
                using var stage = PdfProcessingDiagnostics.Measure(PdfStage.Process);
                await processAsync(files, timeout.Token);
                stage.Complete();
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                diagnostics.Outcome = PdfOutcome.Timeout;
                await Results.Problem(statusCode: 504, title: images
                    ? "画像からPDFへの変換が制限時間を超過しました。" : "PDF処理が制限時間を超過しました。").ExecuteAsync(context);
                return;
            }
            using (var stage = PdfProcessingDiagnostics.Measure(PdfStage.Send))
            {
                await SendFileAsync(context, files.OutputPath, downloadFileName, operation.Token, onSend, contentType);
                stage.Complete();
            }
            diagnostics.Outcome = PdfOutcome.Success;
        }
        catch (PdfFillTemplateException exception)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            await Results.Problem(statusCode: 422, title: exception.Title,
                extensions: new Dictionary<string, object?> { ["reason"] = exception.Code }).ExecuteAsync(context);
        }
        catch (PdfRedactException exception)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            await Results.Problem(statusCode: 422, title: exception.Title,
                extensions: new Dictionary<string, object?> { ["reason"] = exception.Code }).ExecuteAsync(context);
        }
        catch (PdfStampImageException exception)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            var complex = exception.Reason == PdfStampImageReason.TooComplex;
            await Results.Problem(statusCode: 422,
                title: complex ? "印影画像の描画が処理上限を超えています。画像を小さくするか単純にしてください。" :
                    "この印影画像は処理できません。PNGの形式、破損や画像の大きさを確認してください。",
                extensions: new Dictionary<string, object?> { ["reason"] = complex ? "image-too-complex" : "unsupported-image" })
                .ExecuteAsync(context);
        }
        catch (PdfImageInputException exception)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            var (title, reason) = exception.Reason switch
            {
                PdfImageReason.Empty => ("空の画像は処理できません。", (string?)null),
                PdfImageReason.Unsupported => ("この画像は処理できません。JPEG・PNGの形式、破損や画像の大きさを確認してください。", "unsupported-image"),
                PdfImageReason.Capacity => ("画像から作るPDFまたは一時領域の容量上限を超過しました。画像を小さくするか枚数を減らしてください。", "output-too-large"),
                _ => throw new InvalidOperationException("Unknown image reason.")
            };
            await Results.Problem(statusCode: 422, title: title, extensions: reason is null ? null :
                new Dictionary<string, object?> { ["reason"] = reason }).ExecuteAsync(context);
        }
        catch (PdfUnlockException exception)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
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
            diagnostics.Outcome = PdfOutcome.Rejected;
            await Results.Problem(statusCode: 422, title: PdfSplitOutputTooLargeException.Title,
                extensions: new Dictionary<string, object?> { ["reason"] = "output-too-large" }).ExecuteAsync(context);
        }
        catch (PdfCleanTooComplexException)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            await Results.Problem(statusCode: 422, title: "このPDFは情報除去の処理上限を超えています。",
                extensions: new Dictionary<string, object?> { ["reason"] = "too-complex" }).ExecuteAsync(context);
        }
        catch (PdfOverlayInputException exception)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            var complex = exception.Reason == PdfOverlayReason.TooComplex;
            await Results.Problem(statusCode: 422,
                title: complex ? "このPDFは描画の処理上限を超えています。" : "このPDFのページ属性は描画に対応していません。",
                extensions: new Dictionary<string, object?> { ["reason"] = complex ? "too-complex" : "unsupported-pdf" }).ExecuteAsync(context);
        }
        catch (PdfInputException)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            await Results.Problem(statusCode: 422, title: "このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。").ExecuteAsync(context);
        }
        catch (InvalidDataException)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            await Results.Problem(statusCode: 400, title: "multipart/form-data の形式が不正です。").ExecuteAsync(context);
        }
        catch (BadHttpRequestException exception)
        {
            diagnostics.Outcome = PdfOutcome.Rejected;
            var tooLarge = exception.StatusCode == 413;
            await Results.Problem(statusCode: tooLarge ? 413 : 400,
                title: tooLarge ? (tooLargeTitle ?? (images ? "画像またはリクエストのサイズ上限を超過しました。" :
                    "PDFまたはリクエストのサイズ上限を超過しました。")) : "リクエストの形式が不正です。").ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            diagnostics.Outcome = PdfOutcome.ClientCancelled;
            // The process has exited and temporary files have been removed before returning.
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            diagnostics.Outcome = PdfOutcome.Stopping;
            // The process has exited and temporary files have been removed before aborting.
            context.Abort();
        }
        catch (Exception)
        {
            diagnostics.Outcome = PdfOutcome.InternalError;
            if (context.Response.HasStarted)
            {
                diagnostics.Outcome = PdfOutcome.SendAborted;
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
