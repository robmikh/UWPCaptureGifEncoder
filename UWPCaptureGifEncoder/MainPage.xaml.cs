using CaptureEncoder;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace UWPCaptureGifEncoder
{
    public sealed partial class MainPage : Page
    {
        private IDirect3DDevice _device;
        private SemaphoreSlim _semaphore;

        public MainPage()
        {
            this.InitializeComponent();

            _device = Direct3D11Helpers.CreateDevice();
            _semaphore = new SemaphoreSlim(0, 1);

            var appView = ApplicationView.GetForCurrentView();
            if (!appView.IsViewModeSupported(ApplicationViewMode.CompactOverlay))
            {
                OverlayButton.IsEnabled = false;
            }
        }

        private async Task<StorageFile> GetTempFileAsync()
        {
            var folder = ApplicationData.Current.TemporaryFolder;
            var name = DateTime.Now.ToString("yyyyMMdd-HHmm-ss");
            var file = await folder.CreateFileAsync($"{name}.gif");
            return file;
        }

        private async Task<StorageFile> PickGifAsync()
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.SuggestedFileName = "test";
            picker.DefaultFileExtension = ".gif";
            picker.FileTypeChoices.Add("GIF Image", new List<string> { ".gif" });

            var file = await picker.PickSaveFileAsync();
            return file;
        }

        private async void MainToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            // Select what we want to capture
            var picker = new GraphicsCapturePicker();
            var item = await picker.PickSingleItemAsync();

            if (item != null)
            {
                // Get a temporary file to save our gif to
                var file = await GetTempFileAsync();
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    // Get the various d3d objects we'll need
                    var d3dDevice = Direct3D11Helpers.CreateSharpDXDevice(_device);

                    // Create our encoder
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);

                    // Write the application block
                    // http://www.vurdalakov.net/misc/gif/netscape-looping-application-extension
                    var containerProperties = encoder.BitmapContainerProperties;
                    await containerProperties.SetPropertiesAsync(new[] 
                    { 
                        new KeyValuePair<string, BitmapTypedValue>("/appext/application", new BitmapTypedValue(PropertyValue.CreateUInt8Array(Encoding.ASCII.GetBytes("NETSCAPE2.0")), PropertyType.UInt8Array)),
                        // The first value is the size of the block, which is the fixed value 3.
                        // The second value is the looping extension, which is the fixed value 1.
                        // The third and fourth values comprise an unsigned 2-byte integer (little endian).
                        //     The value of 0 means to loop infinitely.
                        // The final value is the block terminator, which is the fixed value 0.
                        new KeyValuePair<string, BitmapTypedValue>("/appext/data", new BitmapTypedValue(PropertyValue.CreateUInt8Array(new byte[] { 3, 1, 0, 0, 0 }), PropertyType.UInt8Array)),
                    });

                    // Setup Windows.Graphics.Capture
                    var itemSize = item.Size;
                    var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                        _device,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        1,
                        itemSize);
                    var session = framePool.CreateCaptureSession(item);

                    // We need a blank texture (background) and a texture that will hold the frame we'll be encoding
                    var description = new SharpDX.Direct3D11.Texture2DDescription
                    {
                        Width = itemSize.Width,
                        Height = itemSize.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new SharpDX.DXGI.SampleDescription()
                        {
                            Count = 1,
                            Quality = 0
                        },
                        Usage = SharpDX.Direct3D11.ResourceUsage.Default,
                        BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget,
                        CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None,
                        OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
                    };
                    var gifTexture = new SharpDX.Direct3D11.Texture2D(d3dDevice, description);
                    var renderTargetView = new SharpDX.Direct3D11.RenderTargetView(d3dDevice, gifTexture);

                    // Encode frames as they arrive. Because we created our frame pool using 
                    // Direct3D11CaptureFramePool::CreateFreeThreaded, this lambda will fire on a different thread
                    // than our current one. If you'd like the callback to fire on your thread, create the frame pool
                    // using Direct3D11CaptureFramePool::Create and make sure your thread has a DispatcherQueue and you
                    // are pumping messages.
                    TimeSpan lastTimeStamp = TimeSpan.MinValue;
                    var frameCount = 0;
                    framePool.FrameArrived += async (s, a) =>
                    {
                        using (var frame = s.TryGetNextFrame())
                        {
                            var contentSize = frame.ContentSize;
                            var timeStamp = frame.SystemRelativeTime;
                            using (var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(frame.Surface))
                            {
                                var width = Math.Clamp(contentSize.Width, 0, itemSize.Width);
                                var height = Math.Clamp(contentSize.Height, 0, itemSize.Height);

                                var region = new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, width, height, 1);

                                d3dDevice.ImmediateContext.ClearRenderTargetView(renderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
                                d3dDevice.ImmediateContext.CopySubresourceRegion(sourceTexture, 0, region, gifTexture, 0);
                            }

                            if (lastTimeStamp == TimeSpan.MinValue)
                            {
                                lastTimeStamp = timeStamp;
                            }
                            var timeStampDelta = timeStamp - lastTimeStamp;
                            lastTimeStamp = timeStamp;
                            var milliseconds = timeStampDelta.TotalMilliseconds;
                            // Use 10ms units
                            var frameDelay = milliseconds / 10;

                            if (frameCount > 0)
                            {
                                await encoder.GoToNextFrameAsync();
                            }

                            // Write our frame delay
                            await encoder.BitmapProperties.SetPropertiesAsync(new[]
                            {
                                new KeyValuePair<string, BitmapTypedValue>("/grctlext/Delay", new BitmapTypedValue(PropertyValue.CreateUInt16((ushort)frameDelay), PropertyType.UInt16)),
                            });

                            // Write the frame to our image
                            var gifSurface = Direct3D11Helpers.CreateDirect3DSurfaceFromSharpDXTexture(gifTexture);
                            var copy = await SoftwareBitmap.CreateCopyFromSurfaceAsync(gifSurface);
                            encoder.SetSoftwareBitmap(copy);
                            frameCount++;
                        }
                    };

                    session.StartCapture();

                    await _semaphore.WaitAsync();

                    session.Dispose();
                    framePool.Dispose();
                    await Task.Delay(1000);

                    await encoder.FlushAsync();

                    var newFile = await PickGifAsync();
                    if (newFile == null)
                    {
                        await file.DeleteAsync();
                        return;
                    }
                    await file.MoveAndReplaceAsync(newFile);

                    await Launcher.LaunchFileAsync(newFile);
                }

            }
        }

        private void MainToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            _semaphore.Release();
        }

        private async void OverlayButton_Click(object sender, RoutedEventArgs e)
        {
            var appView = ApplicationView.GetForCurrentView();
            var viewMode = appView.ViewMode;
            viewMode = viewMode != ApplicationViewMode.CompactOverlay ? ApplicationViewMode.CompactOverlay : ApplicationViewMode.Default;
            await appView.TryEnterViewModeAsync(viewMode);
        }
    }
}
