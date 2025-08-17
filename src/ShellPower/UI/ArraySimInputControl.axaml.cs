using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower;

public partial class ArraySimInputControl : UserControl
{
    private ArraySimulationStepInput? _simInput;
    private bool _updating;

    public ArraySimulationStepInput? SimInput
    {
        get => _simInput;
        set { _simInput = value; UpdateView(); }
    }

    public event EventHandler? Change;

    public ArraySimInputControl()
    {
        InitializeComponent();

        // Hook change handlers (text boxes fire on LostFocus; sliders on PropertyChanged)
        LatBox.LostFocus += AnyInputChanged;
        LonBox.LostFocus += AnyInputChanged;
        TzOffsetBox.LostFocus += AnyInputChanged;
        IrradBox.LostFocus += AnyInputChanged;
        IndirectIrradBox.LostFocus += AnyInputChanged;
        EncapLossBox.LostFocus += AnyInputChanged;

        DirSlider.PropertyChanged += (_, __) => AnyInputChanged(null!, null!);
        TiltSlider.PropertyChanged += (_, __) => AnyInputChanged(null!, null!);
        TimeOfDaySlider.PropertyChanged += (_, __) => TimeOfDayAdjusted();

        UtcDatePicker.SelectedDateChanged += (_, __) => AnyInputChanged(null!, null!);
        UtcTimePicker.SelectedTimeChanged += (_, __) => AnyInputChanged(null!, null!);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void UpdateView()
    {
        if (_simInput == null) return;
        _updating = true;
        try
        {
            var inv = CultureInfo.InvariantCulture;

            // lat/lon
            LatBox.Text = _simInput.Latitude.ToString("0.##########", inv);
            LonBox.Text = _simInput.Longitude.ToString("0.##########", inv);

            // heading label from 16-wind compass
            string[] headings = { "N","NNE","NE","ENE","E","ESE","SE","SSE","S","SSW","SW","WSW","W","WNW","NW","NNW" };
            int dirIx = (int)Math.Round(_simInput.Heading / (2 * Math.PI) * 16);
            if (dirIx >= headings.Length) dirIx -= headings.Length;
            CarDirectionLabel.Text = $"{Astro.rad2deg(_simInput.Heading):0.00}° {headings[dirIx]}";

            // slider discrete 0..15
            int dirIx2 = (int)Math.Round(_simInput.Heading / (2 * Math.PI) * (DirSlider.Maximum + 1));
            if (dirIx2 > DirSlider.Maximum) dirIx2 -= (int)DirSlider.Maximum + 1;
            DirSlider.Value = dirIx2;

            // tilt
            double tiltDeg = Astro.rad2deg(Math.Abs(_simInput.Tilt));
            string tiltDir = _simInput.Tilt > 0 ? "right" : _simInput.Tilt < 0 ? "left" : string.Empty;
            TiltLabel.Text = $"{tiltDeg:0.00}° {tiltDir}";
            TiltSlider.Value = Astro.rad2deg(_simInput.Tilt); // slider in degrees, -180..180

            // UTC date/time
            var utc = _simInput.Utc; // already UTC
            UtcDatePicker.SelectedDate = utc.Date;
            UtcTimePicker.SelectedTime = new TimeSpan(utc.Hour, utc.Minute, utc.Second);

            // timezone + local time
            TzOffsetBox.Text = _simInput.TimezoneOffsetHours.ToString("0.##########", inv);
            var localTime = utc.AddHours(_simInput.TimezoneOffsetHours);
            LocalTimeLabel.Text = localTime.ToString("HH:mm:ss");
            TimeOfDaySlider.Value = (int)(localTime.TimeOfDay.TotalMinutes);

            // conditions
            IrradBox.Text = _simInput.Irradiance.ToString("0.##########", inv);
            IndirectIrradBox.Text = _simInput.IndirectIrradiance.ToString("0.##########", inv);
            EncapLossBox.Text = (_simInput.Array.EncapsulationLoss * 100).ToString("0.##########", inv);

            ErrorLabel.Text = string.Empty;
        }
        finally
        {
            _updating = false;
        }
    }

    public void UpdateModel()
    {
        if (_simInput == null || _updating) return;
        var inv = CultureInfo.InvariantCulture;

        try
        {
            // get location
            if (!double.TryParse(LatBox.Text, NumberStyles.Float, inv, out var lat) ||
                !double.TryParse(LonBox.Text, NumberStyles.Float, inv, out var lon))
                throw new Exception("Latitude/Longitude must be numbers.");
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                throw new Exception("Latitude must be in [-90,90] and longitude in [-180,180].");

            // timezone
            if (!double.TryParse(TzOffsetBox.Text, NumberStyles.Float, inv, out var tzOffset))
                throw new Exception("Timezone offset must be a number.");
            if (tzOffset < -15 || tzOffset > 15)
                throw new Exception("Timezone offset must be in [-15,15].");

            // UTC from pickers
            var date = UtcDatePicker.SelectedDate ?? DateTimeOffset.Now.Date;
            var time = UtcTimePicker.SelectedTime ?? TimeSpan.Zero;
            var utcTime = new DateTime(date.Year, date.Month, date.Day, time.Hours, time.Minutes, time.Seconds, DateTimeKind.Utc);

            // orientation
            double heading = 2 * Math.PI * (DirSlider.Value / (DirSlider.Maximum + 1));
            double tilt = Math.PI * (TiltSlider.Value / 180.0);

            // conditions
            if (!double.TryParse(IrradBox.Text, NumberStyles.Float, inv, out var irrad) ||
                !double.TryParse(IndirectIrradBox.Text, NumberStyles.Float, inv, out var indirect))
                throw new Exception("Irradiance values must be numbers.");
            if (irrad < 0 || irrad > 2000 || indirect < 0 || indirect > 2000)
                throw new Exception("Irradiance must be between 0 and 2000 W/m².");

            if (!double.TryParse(EncapLossBox.Text, NumberStyles.Float, inv, out var encapPct))
                throw new Exception("Encapsulation loss must be a number (%).");
            var encapLoss = encapPct / 100.0;
            if (encapLoss < 0 || encapLoss > 1)
                throw new Exception("Encapsulation loss must be between 0% and 100%.");

            // apply to model
            _simInput.Heading = heading;
            _simInput.Tilt = tilt;
            _simInput.Latitude = lat;
            _simInput.Longitude = lon;
            _simInput.TimezoneOffsetHours = tzOffset;
            _simInput.Utc = utcTime;
            _simInput.Irradiance = irrad;
            _simInput.IndirectIrradiance = indirect;
            _simInput.Array.EncapsulationLoss = encapLoss;

            Logger.info("sim inputs\n\t" +
                "lat {0:0.0} lon {1:0.0} heading {2:0.0} tilt {3:0.0} utc {4} sidereal {5}",
                _simInput.Latitude,
                _simInput.Longitude,
                Astro.rad2deg(_simInput.Heading),
                Astro.rad2deg(_simInput.Tilt),
                utcTime,
                Astro.sidereal_time(utcTime, _simInput.Longitude));

            // Update dependent labels
            UpdateView();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
        }
    }

    private void TimeOfDayAdjusted()
    {
        if (_simInput == null || _updating) return;
        try
        {
            var minutes = (int)TimeOfDaySlider.Value;
            var utc = _simInput.Utc; // keep date from UTC, adjust local TOD
            var local = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Unspecified)
                .AddMinutes(minutes);
            _simInput.Utc = local.AddHours(-_simInput.TimezoneOffsetHours);
            UpdateView();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
        }
        Change?.Invoke(this, EventArgs.Empty);
    }

    private void AnyInputChanged(object? sender, RoutedEventArgs? e)
    {
        UpdateModel();
        Change?.Invoke(this, EventArgs.Empty);
    }
}
