using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;

namespace SSCP.ShellPower
{
    /// <summary>
    /// Convenience functions for accessing the web.
    /// </summary>
    public static class Web
    {
        public static string Get(string url)
        {
            Debug.WriteLine("Getting " + url);
            using var stream = GetStream(url);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static Stream GetStream(string url)
        {
            var request = WebRequest.Create(url);
            var response = request.GetResponse();
            return response.GetResponseStream();
        }

        public static T GetJson<T>(string url)
        {
            var json = Get(url);
            // System.Text.Json by default is case-sensitive
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;
        }
    }
}