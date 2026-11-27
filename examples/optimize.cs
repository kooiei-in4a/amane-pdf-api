#:property PublishAot=false

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("使い方: dotnet run --file examples/optimize.cs -- input.pdf output.pdf");
    return 1;
}

using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(60)
};

try
{
    using var form = new MultipartFormDataContent();
    using var file = new StreamContent(File.OpenRead(args[0]));
    file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    form.Add(file, "file", Path.GetFileName(args[0]));

    using var response = await client.PostAsync("http://127.0.0.1:8080/api/pdf/optimize", form);
    string? mediaType = response.Content.Headers.ContentType?.MediaType;
    if (response.StatusCode == HttpStatusCode.OK &&
        string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
    {
        await File.WriteAllBytesAsync(args[1], await response.Content.ReadAsByteArrayAsync());
        return 0;
    }

    Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");
    if (string.Equals(mediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            if (problem.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "title", "reason" })
                {
                    if (problem.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        Console.Error.WriteLine($"{key}: {value.GetString()}");
                }
            }
        }
        catch (JsonException)
        {
            // JSON を読めない場合は HTTP ステータスだけを使います。
        }
    }
}
catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
{
    Console.Error.WriteLine("API との通信に失敗しました。");
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("ファイルの読み書きに失敗しました。");
}
return 1;
