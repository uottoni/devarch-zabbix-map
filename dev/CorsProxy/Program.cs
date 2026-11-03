// Proxy de desenvolvimento: o Zabbix não envia cabeçalhos CORS, então o navegador bloqueia
// chamadas vindas do app WebAssembly. Este proxy escuta só em localhost, aceita só origens
// localhost e repassa o JSON-RPC para um único servidor Zabbix.
//
// Uso: dotnet run --project dev/CorsProxy -- https://zabbix.exemplo.com.br [--porta 8089] [--ignorar-certificado]
// No app, conecte em http://localhost:8089.
using System.Net;

var target = args.FirstOrDefault(a => !a.StartsWith("--"));
if (target == null)
{
    Console.Error.WriteLine("Informe a URL do Zabbix: dotnet run --project dev/CorsProxy -- https://zabbix.exemplo.com.br");
    return 1;
}
var port = args.SkipWhile(a => a != "--porta").Skip(1).FirstOrDefault() is { } p ? int.Parse(p) : 8089;
var endpoint = new Uri(target.TrimEnd('/').Replace("/api_jsonrpc.php", "") + "/api_jsonrpc.php");
var handler = new HttpClientHandler();
if (args.Contains("--ignorar-certificado"))
    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

using var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine($"Repassando http://localhost:{port}/api_jsonrpc.php -> {endpoint}");

while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(context));
}

async Task HandleAsync(HttpListenerContext context)
{
    var (request, response) = (context.Request, context.Response);
    try
    {
        var origin = request.Headers["Origin"];
        if (origin != null)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !originUri.IsLoopback)
            {
                response.StatusCode = 403;
                return;
            }
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
            response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            response.Headers["Vary"] = "Origin";
        }
        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            return;
        }
        if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/api_jsonrpc.php")
        {
            response.StatusCode = 404;
            return;
        }

        // Buffered so the upstream request carries Content-Length instead of a chunked body.
        using var body = new MemoryStream();
        await request.InputStream.CopyToAsync(body);
        using var forward = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body.ToArray()) };
        forward.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType ?? "application/json-rpc");
        if (request.Headers["Authorization"] is { } auth) forward.Headers.TryAddWithoutValidation("Authorization", auth);

        using var upstream = await http.SendAsync(forward);
        response.StatusCode = (int)upstream.StatusCode;
        response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
        await upstream.Content.CopyToAsync(response.OutputStream);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or HttpListenerException or IOException)
    {
        Console.Error.WriteLine($"{request.HttpMethod} {request.Url}: {e.Message}");
        if (response.OutputStream.CanWrite) response.StatusCode = 502;
    }
    finally
    {
        response.Close();
    }
}
