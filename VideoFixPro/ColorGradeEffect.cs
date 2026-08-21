using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace VideoFixPro;

public class ColorGradeEffect : ShaderEffect
{
    private static readonly PixelShader _pixelShader = new()
    {
        UriSource = new Uri("pack://application:,,,/VideoFixPro;component/ColorGradeEffect.ps")
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(ColorGradeEffect), 0);

    public static readonly DependencyProperty BrightnessProperty =
        DependencyProperty.Register(nameof(Brightness), typeof(double), typeof(ColorGradeEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty ContrastProperty =
        DependencyProperty.Register(nameof(Contrast), typeof(double), typeof(ColorGradeEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty GammaProperty =
        DependencyProperty.Register(nameof(Gamma), typeof(double), typeof(ColorGradeEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty SaturationProperty =
        DependencyProperty.Register(nameof(Saturation), typeof(double), typeof(ColorGradeEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty TemperatureProperty =
        DependencyProperty.Register(nameof(Temperature), typeof(double), typeof(ColorGradeEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(double), typeof(ColorGradeEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty VignetteProperty =
        DependencyProperty.Register(nameof(Vignette), typeof(double), typeof(ColorGradeEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(6)));

    public ColorGradeEffect()
    {
        PixelShader = _pixelShader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(BrightnessProperty);
        UpdateShaderValue(ContrastProperty);
        UpdateShaderValue(GammaProperty);
        UpdateShaderValue(SaturationProperty);
        UpdateShaderValue(TemperatureProperty);
        UpdateShaderValue(TintProperty);
        UpdateShaderValue(VignetteProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double Brightness
    {
        get => (double)GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    public double Contrast
    {
        get => (double)GetValue(ContrastProperty);
        set => SetValue(ContrastProperty, value);
    }

    public double Gamma
    {
        get => (double)GetValue(GammaProperty);
        set => SetValue(GammaProperty, value);
    }

    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public double Temperature
    {
        get => (double)GetValue(TemperatureProperty);
        set => SetValue(TemperatureProperty, value);
    }

    public double Tint
    {
        get => (double)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public double Vignette
    {
        get => (double)GetValue(VignetteProperty);
        set => SetValue(VignetteProperty, value);
    }
}
