using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace N64RecompLauncher
{
    public class AnimatedGifImage : System.Windows.Controls.Image
    {
        private Bitmap? _bitmap;
        private BitmapSource[]? _bitmapSources;
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

        private void AnimatedGifImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (GifSource != null)
                SetImageGifSource();
        }

        private void AnimatedGifImage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAnimation();
            _bitmap?.Dispose();
            _bitmap = null;
        }

        public string? GifSource
        {
            get => (string?)GetValue(GifSourceProperty);
            set => SetValue(GifSourceProperty, value);
        }

        public static readonly DependencyProperty GifSourceProperty =
            DependencyProperty.Register("GifSource", typeof(string), typeof(AnimatedGifImage),
                new PropertyMetadata(null, GifSourcePropertyChanged));

        private static void GifSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var gif = d as AnimatedGifImage;
            gif?.SetImageGifSource();
        }

        private void SetImageGifSource()
        {
            if (GifSource == null) return;

            try
            {
                _bitmap?.Dispose();

                var uri = new Uri($"pack://application:,,,{GifSource}", UriKind.Absolute);
                var resourceStream = Application.GetResourceStream(uri);

                if (resourceStream?.Stream != null)
                {
                    var memoryStream = new MemoryStream();
                    resourceStream.Stream.CopyTo(memoryStream);
                    memoryStream.Position = 0;

                    _bitmap = new Bitmap(memoryStream);

                    if (System.Drawing.ImageAnimator.CanAnimate(_bitmap))
                    {
                        PrepareAnimation();
                        StartAnimation();
                    }
                    else
                    {
                        var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            _bitmap.GetHbitmap(),
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        Source = bitmapSource;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading GIF: {ex.Message}");
            }
        }

        private void PrepareAnimation()
        {
            if (_bitmap == null) return;

            var dimension = new System.Drawing.Imaging.FrameDimension(_bitmap.FrameDimensionsList[0]);
            var frameCount = _bitmap.GetFrameCount(dimension);
            _bitmapSources = new BitmapSource[frameCount];

            for (int i = 0; i < frameCount; i++)
            {
                _bitmap.SelectActiveFrame(dimension, i);
                var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    _bitmap.GetHbitmap(),
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                _bitmapSources[i] = bitmapSource;
            }

            var propItem = _bitmap.GetPropertyItem(0x5100);
            var frameDelay = BitConverter.ToInt32(propItem.Value, 0) * 10;
            if (frameDelay == 0) frameDelay = 100; 

            _timer.Interval = TimeSpan.FromMilliseconds(frameDelay);
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