using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VisionAICam.Helpers
{
    public class MjpegStreamReader
    {
        private CancellationTokenSource _cts;
        private readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private Task _streamTask;

        /// <summary>
        /// Start MJPEG stream reader with BGR to RGB conversion
        /// </summary>
        /// <param name="url">MJPEG stream URL</param>
        /// <param name="onFrame">Callback for each decoded frame</param>
        public async Task StartAsync(string url, Action<BitmapImage> onFrame)
        {
            Stop();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _streamTask = Task.Run(async () =>
            {
                HttpResponseMessage response = null;
                Stream stream = null;

                try
                {
                    response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    stream = await response.Content.ReadAsStreamAsync();

                    byte[] buffer = new byte[1024 * 1024];
                    using MemoryStream jpgStream = new MemoryStream();

                    while (!token.IsCancellationRequested)
                    {
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

                        if (bytesRead <= 0)
                        {
                            Debug.WriteLine("[MJPEG] Stream ended");
                            break;
                        }

                        // Find JPEG markers
                        int start = Find(buffer, bytesRead, new byte[] { 0xFF, 0xD8 });
                        int end = Find(buffer, bytesRead, new byte[] { 0xFF, 0xD9 });

                        if (start >= 0 && end > start)
                        {
                            int len = end - start + 2;
                            jpgStream.SetLength(0);
                            jpgStream.Write(buffer, start, len);
                            jpgStream.Position = 0;

                            try
                            {
                                // Decode JPEG to BitmapImage
                                var img = new BitmapImage();
                                img.BeginInit();
                                img.CacheOption = BitmapCacheOption.OnLoad;
                                img.StreamSource = jpgStream;
                                img.EndInit();
                                img.Freeze();

                                // ✅ Convert BGR to RGB by swapping R and B channels
                                var rgbImage = SwapRedBlueChannels(img);

                                onFrame?.Invoke(img);
                            }
                            catch (Exception imgEx)
                            {
                                Debug.WriteLine($"[MJPEG] Frame decode error: {imgEx.Message}");
                            }
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    Debug.WriteLine("[MJPEG] Stream cancelled");
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[MJPEG] Operation cancelled");
                }
                catch (HttpRequestException httpEx)
                {
                    Debug.WriteLine($"[MJPEG-HTTP-ERROR] {httpEx.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MJPEG-ERROR] {ex.Message}");
                }
                finally
                {
                    try { stream?.Dispose(); } catch { }
                    try { response?.Dispose(); } catch { }
                }

            }, token);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Swap Red and Blue channels (BGR ↔ RGB)
        /// </summary>
        private BitmapImage SwapRedBlueChannels(BitmapSource source)
        {
            try
            {
                int width = source.PixelWidth;
                int height = source.PixelHeight;
                int stride = width * 4; // 32bpp BGRA

                byte[] pixels = new byte[height * stride];
                source.CopyPixels(pixels, stride, 0);

                // Swap R and B channels (BGRA → RGBA)
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte blue = pixels[i];
                    byte red = pixels[i + 2];
                    pixels[i] = red;      // B ← R
                    pixels[i + 2] = blue; // R ← B
                    // pixels[i + 1] is G (unchanged)
                    // pixels[i + 3] is A (unchanged)
                }

                // Create new BitmapSource with swapped channels
                var bitmap = BitmapSource.Create(
                    width, height,
                    96, 96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);

                // Convert to BitmapImage for WPF
                var result = new BitmapImage();
                using (var ms = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(ms);
                    ms.Position = 0;

                    result.BeginInit();
                    result.CacheOption = BitmapCacheOption.OnLoad;
                    result.StreamSource = ms;
                    result.EndInit();
                    result.Freeze();
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MJPEG-SWAP-ERROR] {ex.Message}");
                return source as BitmapImage ?? ConvertToBitmapImage(source);
            }
        }

        /// <summary>
        /// Convert BitmapSource to BitmapImage (fallback)
        /// </summary>
        private BitmapImage ConvertToBitmapImage(BitmapSource source)
        {
            try
            {
                var result = new BitmapImage();
                using (var ms = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(source));
                    encoder.Save(ms);
                    ms.Position = 0;

                    result.BeginInit();
                    result.CacheOption = BitmapCacheOption.OnLoad;
                    result.StreamSource = ms;
                    result.EndInit();
                    result.Freeze();
                }
                return result;
            }
            catch
            {
                return new BitmapImage();
            }
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MJPEG-STOP-ERROR] {ex.Message}");
            }
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

        public void Dispose()
        {
            Stop();
            try { _http?.Dispose(); } catch { }
        }
    }
}