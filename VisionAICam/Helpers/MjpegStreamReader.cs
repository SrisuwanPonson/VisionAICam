using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

public class MjpegStreamReader
{
    private CancellationTokenSource cts;
    private readonly HttpClient http = new HttpClient();

    public async Task StartAsync(string url, Action<BitmapImage> onFrame)
    {
        cts = new CancellationTokenSource();

        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();

        byte[] buffer = new byte[1024 * 1024];
        MemoryStream jpgStream = new MemoryStream();

        while (!cts.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
                continue;

            // หา JPEG header/footer
            int start = Find(buffer, bytesRead, new byte[] { 0xFF, 0xD8 });
            int end = Find(buffer, bytesRead, new byte[] { 0xFF, 0xD9 });

            if (start >= 0 && end > start)
            {
                int len = end - start + 2;
                jpgStream.SetLength(0);
                jpgStream.Write(buffer, start, len);
                jpgStream.Position = 0;

                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = jpgStream;
                img.EndInit();
                img.Freeze();

                onFrame?.Invoke(img);
            }
        }
    }

    public void Stop()
    {
        cts?.Cancel();
    }

    private int Find(byte[] buffer, int length, byte[] pattern)
    {
        for (int i = 0; i < length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}
