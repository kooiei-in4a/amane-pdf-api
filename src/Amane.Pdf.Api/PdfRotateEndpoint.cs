using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfRotateEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        try
        {
            var angleValues = context.Request.Query["angle"];
            if (angleValues.Count != 1) throw new BadHttpRequestException("Invalid angle query.");
            var angle = angleValues[0] switch
            {
                "90" => 90,
                "180" => 180,
                "270" => 270,
                _ => throw new BadHttpRequestException("Invalid angle query.")
            };

            PdfPageSelection? selection = null;
            if (context.Request.Query.TryGetValue("pages", out var pageValues))
            {
                if (pageValues.Count != 1) throw new BadHttpRequestException("Invalid pages query.");
                selection = PdfPageSelection.Parse(pageValues[0] ?? string.Empty);
            }

            PdfEndpointHelpers.ApplyRequestSizeLimit(context, options.Value);
            using var files = new TemporaryPdfFiles(options.Value.TempRoot);
            await MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, context.RequestAborted);
            var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.QpdfTimeoutSeconds));
            try
            {
                await processor.ValidateAsync(files, timeout.Token);
                var pageRange = selection is null
                    ? null
                    : selection.ToQpdfRange(await processor.GetPageCountAsync(files, timeout.Token));
                await processor.RotateAsync(files, angle, pageRange, timeout.Token);
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
            await PdfEndpointHelpers.SendPdfAsync(context, files.OutputPath, "rotated.pdf");
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
                .CreateLogger("PdfRotate").LogError("PDF処理で内部障害が発生しました。");
            context.Response.Clear();
            await Results.Problem(statusCode: 500, title: "PDF処理に失敗しました。").ExecuteAsync(context);
        }
    }
}
