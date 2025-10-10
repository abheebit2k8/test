using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PatSnapProxy
{
    public class PatSnapProxy
    {
        private static readonly HttpClient _http = new HttpClient();
        private static string? _cachedToken;
        private static DateTime _tokenExpiry = DateTime.MinValue;

        // Replace with your real credentials
        private const string ClientId = "jvito1qhroouvho0clo9e6d2dc2u0ldxbznvcbioevxmxguj";
        private const string ClientSecret = "j2dkjyb504jnj54z0sg7sefzohd0ah55qu4p68x5gtoruma28l693zl1j0nolmym";

        [Function("PatSnapProxy")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous,
                         "get","post","put","delete",
                         Route = "PatSnapProxy/{*path}")]
            HttpRequestData req, string path)
        {
            // 1) OAuth token refresh (unchanged)...
            if (_cachedToken == null || DateTime.UtcNow >= _tokenExpiry)
            {
                var tokenReq = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://connect.patsnap.com/oauth/token")
                {
                    Content = new StringContent(
                        "grant_type=client_credentials",
                        System.Text.Encoding.UTF8,
                        "application/x-www-form-urlencoded")
                };
                var basicAuth = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
                tokenReq.Headers.Authorization =
                    new AuthenticationHeaderValue("Basic", basicAuth);

                var tokenResp = await _http.SendAsync(tokenReq);
                tokenResp.EnsureSuccessStatusCode();

                using var ts = await tokenResp.Content.ReadAsStreamAsync();
                using var td = await JsonDocument.ParseAsync(ts);
                _cachedToken = td.RootElement.GetProperty("data")
                                            .GetProperty("token")
                                            .GetString();
                var expiresIn = int.Parse(td.RootElement
                                            .GetProperty("data")
                                            .GetProperty("expires_in")
                                            .GetString() ?? "1800");
                _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            }

            // 2) New PDF‐stream branch: GET /basic-patent-data/pdf-stream?patent_id={id}
            const string streamPrefix = "basic-patent-data/pdf-stream";
            if (!string.IsNullOrEmpty(path)
                && path.StartsWith(streamPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // a) Pull presigned JSON
                var qs = req.Url.Query;
                var detailU = $"https://connect.patsnap.com/basic-patent-data/pdf-data{qs}&apikey={ClientId}";
                var detailRq = new HttpRequestMessage(HttpMethod.Get, detailU);
                detailRq.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _cachedToken);
                // disable conditional GETs
                detailRq.Headers.IfModifiedSince = null;
                detailRq.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                var detailRs = await _http.SendAsync(detailRq);
                detailRs.EnsureSuccessStatusCode();
                using var ds = await detailRs.Content.ReadAsStreamAsync();
                using var dj = await JsonDocument.ParseAsync(ds);
                var firstElem = dj.RootElement
                                 .GetProperty("data")
                                 .EnumerateArray()
                                 .FirstOrDefault();

                if (!firstElem.TryGetProperty("pdf", out var pdfElem) ||
                    !pdfElem.TryGetProperty("path", out var pathElem))
                {
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteStringAsync("No PDF URL found.");
                    return nf;
                }

                // b) Decode & fetch the PDF bytes
                var rawUrl = pathElem.GetString()!;
                var pdfUrl = rawUrl.Replace("\\u0026", "&");
                var pdfReq = new HttpRequestMessage(HttpMethod.Get, pdfUrl);
                // again, no conditional GET
                pdfReq.Headers.IfModifiedSince = null;
                pdfReq.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                var pdfResp = await _http.SendAsync(pdfReq,
                                   HttpCompletionOption.ResponseHeadersRead);
                // allow 200 or 206
                if (pdfResp.StatusCode is not HttpStatusCode.OK and
                                         not HttpStatusCode.PartialContent)
                    pdfResp.EnsureSuccessStatusCode();

                var clientPdf = req.CreateResponse(pdfResp.StatusCode);
                clientPdf.Headers.Add("Content-Type", "application/pdf");
                clientPdf.Headers.Add("Accept-Ranges", "bytes");
                clientPdf.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                clientPdf.Headers.Add("X-Content-Type-Options", "nosniff");
                // forward Content-Range if partial
                if (pdfResp.Content.Headers.ContentRange is { } cr)
                    clientPdf.Headers.Add("Content-Range", cr.ToString());

                // stream bytes straight to the HTTP response
                await (await pdfResp.Content.ReadAsStreamAsync())
                    .CopyToAsync(clientPdf.Body);
                return clientPdf;
            }

            // 3) HTML wrapper branch (very thin) at /basic-patent-data/pdf-data
            // Inside your Run(...) method, replace the old HTML branch with this:

            const string pdfPrefix = "basic-patent-data/pdf-data";
            if (!string.IsNullOrEmpty(path)
                && path.StartsWith(pdfPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // 1) Fetch presigned JSON URL
                var query = req.Url.Query;
                var detailU = $"https://connect.patsnap.com/{pdfPrefix}{query}&apikey={ClientId}";
                var detailRq = new HttpRequestMessage(HttpMethod.Get, detailU);
                detailRq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);

                var detailRs = await _http.SendAsync(detailRq);
                detailRs.EnsureSuccessStatusCode();

                using var jstream = await detailRs.Content.ReadAsStreamAsync();
                using var jdoc = await JsonDocument.ParseAsync(jstream);
                var item = jdoc.RootElement
                               .GetProperty("data")
                               .EnumerateArray()
                               .FirstOrDefault();

                if (!item.TryGetProperty("pdf", out var p) ||
                    !p.TryGetProperty("path", out var pathEl))
                {
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteStringAsync("No PDF link found.");
                    return nf;
                }

                // 2) Clean up the URL
                var rawUrl = pathEl.GetString()!;
                var pdfUrl = rawUrl.Replace("\\u0026", "&");

                // 3) Return an HTML page using PDF.js
                // In your HTML-wrapper branch, after you’ve computed pdfUrl:
                var streamUrl =
    $"{req.Url.Scheme}://{req.Url.Host}/api/PatSnapProxy/basic-patent-data/pdf-stream{req.Url.Query}";

                var html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>Patent PDF Viewer</title>
  <style>
    body { margin:0; background:#333; color:#fff; font-family:sans-serif; }
    #loader {
      position:absolute; top:50%; left:50%; transform:translate(-50%,-50%);
      display:flex; flex-direction:column; align-items:center;
    }
    .spinner {
      border: 6px solid rgba(255,255,255,0.2);
      border-top: 6px solid #03a9f4;
      border-radius: 50%;
      width: 48px; height: 48px;
      animation: spin 1s linear infinite;
      margin-bottom: 12px;
    }
    @keyframes spin { 0% { transform: rotate(0deg); } 100% { transform: rotate(360deg); } }

    #progress-text { font-size:14px; }

    #viewer {
      display:none;
      width:100%; height:100vh; overflow:auto;
      background:#fff;
    }
    canvas { display:block; margin:16px auto; box-shadow:0 0 4px rgba(0,0,0,0.3); }
  </style>
  <script src=""https://cdnjs.cloudflare.com/ajax/libs/pdf.js/2.16.105/pdf.min.js""></script>
</head>
<body>
  <div id=""loader"">
    <div class=""spinner""></div>
    <div id=""progress-text"">Loading PDF…</div>
  </div>
  <div id=""viewer""></div>
  <script>
    const url = '" + streamUrl + @"';
    const loader = document.getElementById('loader');
    const progressText = document.getElementById('progress-text');
    const container = document.getElementById('viewer');

    // kick off download + render
    const loadingTask = pdfjsLib.getDocument({ url });
    loadingTask.onProgress = function(progress) {
      if (progress.total) {
        const pct = Math.round((progress.loaded / progress.total) * 100);
        progressText.textContent = 'Loading PDF: ' + pct + '%';
      }
    };

    loadingTask.promise
      .then(function(pdf) {
        // hide loader, show viewer
        loader.style.display = 'none';
        container.style.display = 'block';

        // render each page
        for (let i = 1; i <= pdf.numPages; i++) {
          pdf.getPage(i).then(function(page) {
            const vp = page.getViewport({ scale: 1.2 });
            const canvas = document.createElement('canvas');
            canvas.width = vp.width; canvas.height = vp.height;
            container.appendChild(canvas);
            page.render({ canvasContext: canvas.getContext('2d'), viewport: vp });
          });
        }
      })
      .catch(function(err) {
        loader.innerHTML =
          '<pre style=""color:#f00; text-align:center; padding:20px;"">' +
          'Error loading PDF:\n' + err.message +
          '</pre>';
      });
  </script>
</body>
</html>";

                var resp = req.CreateResponse(HttpStatusCode.OK);
                resp.Headers.Add("Content-Type", "text/html; charset=UTF-8");
                await resp.WriteStringAsync(html);
                return resp;

            }

            // ──────────────── Claim‐Data Branch ────────────────
            const string claimDataPrefix = "basic-patent-data/claim-data";
            if (!string.IsNullOrEmpty(path)
                && path.StartsWith(claimDataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var query = req.Url.Query;  // ?patent_number=… or ?patent_id=…
                var forwardUrl = $"https://connect.patsnap.com/{claimDataPrefix}{query}&apikey={ClientId}";

                var forwardReq = new HttpRequestMessage(HttpMethod.Get, forwardUrl);
                forwardReq.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _cachedToken);

                var forwardResp = await _http.SendAsync(forwardReq);
                var bodyJson = await forwardResp.Content.ReadAsStringAsync();

                var claimResponse = req.CreateResponse(forwardResp.StatusCode);
                claimResponse.Headers.Add("Content-Type", "application/json");
                await claimResponse.WriteStringAsync(bodyJson);
                return claimResponse;
            }

            // ───────────── Bibliography Branch ─────────────
            const string biblioPrefix = "basic-patent-data/bibliography";
            if (!string.IsNullOrEmpty(path)
                && path.StartsWith(biblioPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var query = req.Url.Query; // ?patent_number=… or ?patent_id=…
                var forwardUrl = $"https://connect.patsnap.com/{biblioPrefix}{query}&apikey={ClientId}";

                var forwardReq = new HttpRequestMessage(HttpMethod.Get, forwardUrl);
                forwardReq.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _cachedToken);

                var forwardResp = await _http.SendAsync(forwardReq);
                var bodyJson = await forwardResp.Content.ReadAsStringAsync();

                var biblioResponse = req.CreateResponse(forwardResp.StatusCode);
                biblioResponse.Headers.Add("Content-Type", "application/json");
                await biblioResponse.WriteStringAsync(bodyJson);
                return biblioResponse;
            }

            // ───────────── Generic Pass‐Through ─────────────
            var targetUrl = $"https://connect.patsnap.com/{path}?apikey={ClientId}";
            var proxyReq = new HttpRequestMessage(new HttpMethod(req.Method), targetUrl);

            var payload = await new StreamReader(req.Body).ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(payload))
                proxyReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            proxyReq.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _cachedToken);

            var proxyResp = await _http.SendAsync(proxyReq);
            var proxyJson = await proxyResp.Content.ReadAsStringAsync();

            var finalResponse = req.CreateResponse(proxyResp.StatusCode);
            finalResponse.Headers.Add("Content-Type", "application/json");
            await finalResponse.WriteStringAsync(proxyJson);

            return finalResponse;
        }
    }
}
