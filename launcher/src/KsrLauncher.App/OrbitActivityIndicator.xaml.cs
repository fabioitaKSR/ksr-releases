using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace KsrLauncher.App;

public partial class OrbitActivityIndicator : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(OrbitActivityIndicator),
        new PropertyMetadata(false, (owner, _) => ((OrbitActivityIndicator)owner).RefreshAnimation()));
    private bool _running;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public OrbitActivityIndicator()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => RefreshAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    private void RefreshAnimation()
    {
        Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (!IsActive || !IsVisible) { StopAnimation(); return; }
        if (_running) return;
        _running = true;
        InnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, Orbit(3));
        MiddleRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, Orbit(5));
        OuterRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, Orbit(8));
    }

    private static DoubleAnimation Orbit(int seconds)
    {
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 30);
        return animation;
    }

    private void StopAnimation()
    {
        InnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        MiddleRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        OuterRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        _running = false;
    }
}
