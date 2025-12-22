using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Platform;
using SystemBitmap = System.Drawing.Bitmap;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace N64RecompLauncher
{
    public class AnimatedGifImage : Image
    {
        private SystemBitmap? _bitmap;
        private AvaloniaBitmap[]? _bitmapSources;
        private int _frameIndex;
        private bool _isAnimationWorking;
        private readonly DispatcherTimer _timer;

        public AnimatedGifImage()
        {
            _timer = new DispatcherTimer();
            _timer.Tick += NextFrame;
            Loaded += AnimatedGifImage_Loaded;
            Unloaded += AnimatedGifImage_Unloaded;
        }

        private void AnimatedGifImage_Loaded(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (GifSource != null)
                SetImageGifSource();
        }

        private void AnimatedGifImage_Unloaded(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            StopAnimation();
            _bitmap?.Dispose();
            _bitmap = null;
        }

        public string? GifSource
        {
            get => GetValue(GifSourceProperty);
            set => SetValue(GifSourceProperty, value);
        }

        public static readonly StyledProperty<string?> GifSourceProperty =
            AvaloniaProperty.Register<AnimatedGifImage, string?>(nameof(GifSource),
                defaultValue: null);

        static AnimatedGifImage()
        {
            GifSourceProperty.Changed.AddClassHandler<AnimatedGifImage>((x, e) => x.GifSourcePropertyChanged(e));
        }

        private void GifSourcePropertyChanged(AvaloniaPropertyChangedEventArgs e)
        {
            SetImageGifSource();
        }

        private void SetImageGifSource()
        {
            if (GifSource == null) return;

            try
            {
                _bitmap?.Dispose();

                var assetLoader = AssetLoader.Open(new Uri($"avares://N64RecompLauncher{GifSource}"));

                if (assetLoader != null)
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        assetLoader.CopyTo(memoryStream);
                        memoryStream.Position = 0;

                        _bitmap = new SystemBitmap(memoryStream);

                        if (System.Drawing.ImageAnimator.CanAnimate(_bitmap))
                        {
                            PrepareAnimation();
                            StartAnimation();
                        }
                        else
                        {
                            var avaloniaBitmap = ConvertToAvaloniaBitmap(_bitmap);
                            Source = avaloniaBitmap;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading GIF: {ex.Message}");
            }
        }

        private AvaloniaBitmap ConvertToAvaloniaBitmap(SystemBitmap systemBitmap)
        {
            using var memoryStream = new MemoryStream();
            systemBitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
            memoryStream.Position = 0;
            return new AvaloniaBitmap(memoryStream);
        }

        private void PrepareAnimation()
        {
            if (_bitmap == null) return;

            var dimension = new System.Drawing.Imaging.FrameDimension(_bitmap.FrameDimensionsList[0]);
            var frameCount = _bitmap.GetFrameCount(dimension);
            _bitmapSources = new AvaloniaBitmap[frameCount];

            for (int i = 0; i < frameCount; i++)
            {
                _bitmap.SelectActiveFrame(dimension, i);
                _bitmapSources[i] = ConvertToAvaloniaBitmap(_bitmap);
            }

            try
            {
                var propItem = _bitmap.GetPropertyItem(0x5100);
                var frameDelay = BitConverter.ToInt32(propItem.Value, 0) * 10;
                if (frameDelay == 0) frameDelay = 100;
                _timer.Interval = TimeSpan.FromMilliseconds(frameDelay);
            }
            catch
            {
                _timer.Interval = TimeSpan.FromMilliseconds(100);
            }
        }

        private void StartAnimation()
        {
            if (!_isAnimationWorking && _bitmapSources?.Length > 0)
            {
                _isAnimationWorking = true;
                _frameIndex = 0;
                Source = _bitmapSources[0];
                _timer.Start();
            }
        }

        private void StopAnimation()
        {
            _isAnimationWorking = false;
            _timer.Stop();
        }

        private void NextFrame(object? sender, EventArgs e)
        {
            if (_bitmapSources == null || _bitmapSources.Length == 0) return;

            _frameIndex = (_frameIndex + 1) % _bitmapSources.Length;
            Source = _bitmapSources[_frameIndex];
        }
    }
}