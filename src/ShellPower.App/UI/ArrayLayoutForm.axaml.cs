using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace SSCP.ShellPower;

public partial class ArrayLayoutForm : Window
{
    private readonly ArraySpec array;

    // UI-facing collection
    public ObservableCollection<ArraySpec.CellString> Strings { get; }

    private ArraySpec.CellString? selectedCellString;
    public ArraySpec.CellString? SelectedCellString
    {
        get => selectedCellString;
        set
        {
            if (selectedCellString != value)
            {
                selectedCellString = value;
                ArrayLayoutControl.CellString = value;
                UpdateView();
            }
        }
    }

    public ArrayLayoutForm(ArraySpec spec)
    {
        Debug.Assert(spec != null);
        array = spec;

        // wrap the array's list for UI
        Strings = new ObservableCollection<ArraySpec.CellString>(array.Strings);

        DataContext = this;
        InitializeComponent();
        UpdateView();
    }
    
    private async void ButtonLoadLayout_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters =
            {
                new FileDialogFilter { Name = "Images", Extensions = { "png","jpg","bmp" } }
            }
        };
        var result = await dialog.ShowAsync(this);
        if (result != null && result.Length > 0)
        {
            try
            {
                array.LayoutTexture = SixLabors.ImageSharp.Image.Load<Rgba32>(result[0]);
                array.ReadStringsFromColors();
                UpdateView();
            }
            catch
            {
                await ShowErrorDialog($"Could not open bitmap {result[0]}.\nIs it open in another program? Is it a valid image?");
            }
        }
    }

    private void ButtonEdit_Click(object? sender, RoutedEventArgs e)
    {
        ArrayLayoutControl.Editable = !ArrayLayoutControl.Editable;
        if (!ArrayLayoutControl.Editable && ArrayLayoutControl.CellString != null)
        {
            // just finished editing
            var editedStr = ArrayLayoutControl.CellString;
            array.Strings.RemoveAll(cellStr =>
            {
                if (cellStr != editedStr)
                {
                    cellStr.Cells.RemoveAll(cell => editedStr.Cells.Contains(cell));
                }
                return cellStr.Cells.Count == 0;
            });

            if (array.LayoutTexture != null)
            {
                array.Recolor();
            }
        }
        
        UpdateView();
    }

    private void ButtonCreateString_Click(object? sender, RoutedEventArgs e)
    {
        var newString = new ArraySpec.CellString();
        array.Strings.Add(newString);
        ArrayLayoutControl.CellString = newString;
        ArrayLayoutControl.Editable = true;
        ArrayLayoutControl.AnimatedSelection = true;
        SelectedCellString = newString;
    }

    private void ButtonDeleteString_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedCellString != null)
        {
            array.Strings.Remove(SelectedCellString);
            SelectedCellString = null;
            UpdateView();
        }
    }

    private void ArrayLayoutControl_CellStringChanged(object? sender, EventArgs e)
    {
        // refresh list display (name updates)
        UpdateStrings();
    }

    private void ButtonOK_Click(object? sender, RoutedEventArgs e)
    {
        if (array.LayoutTexture != null)
            array.Recolor();
        Close();
    }

    private void ButtonCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CheckBoxEditDiodes_Changed(object? sender, RoutedEventArgs e)
    {
        UpdateView();
    }

    // --- Helpers ---

    private void UpdateView()
    {
        UpdateArrayLayout();
        UpdateStrings();
        UpdateControls();
    }

    private void UpdateArrayLayout()
    {
        bool hasLayout = (array.LayoutTexture != null);
        ArrayLayoutControl.IsVisible = hasLayout;
        if (!hasLayout) return;

        ArrayLayoutControl.Array = array;
        ArrayLayoutControl.EditBypassDiodes = CheckBoxEditDiodes.IsChecked == true;
    }

    private void UpdateStrings()
    {
        for (int i = 0; i < array.Strings.Count; i++)
        {
            array.Strings[i].Name = $"String {i + 1}";
        }
        // ListBox is bound, so no need to touch ListBoxStrings.Items manually
    }

    private void UpdateControls()
    {
        if (ArrayLayoutControl.Editable && SelectedCellString != null)
        {
            ButtonEdit.Content = "Done";
            ButtonEdit.IsEnabled = true;
            ButtonCreateString.IsEnabled = false;
            ButtonDeleteString.IsEnabled = false;
            LabelMakeString.IsVisible = false;
            LabelExplain.IsVisible = true;
            CheckBoxEditDiodes.IsVisible = true;
            ListBoxStrings.IsEnabled = false;
        }
        else
        {
            ButtonEdit.Content = "Edit";
            ButtonEdit.IsEnabled = SelectedCellString != null;
            ButtonCreateString.IsEnabled = true;
            ButtonDeleteString.IsEnabled = SelectedCellString != null;
            LabelMakeString.IsVisible = true;
            LabelExplain.IsVisible = false;
            CheckBoxEditDiodes.IsVisible = false;
            CheckBoxEditDiodes.IsChecked = false;
            ArrayLayoutControl.Editable = false;
            ListBoxStrings.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task ShowErrorDialog(string message, string title = "Error")
    {
        var dlg = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0,0,0,20) },
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, IsDefault = true }
                }
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (dlg.Content is StackPanel sp && sp.Children[1] is Button okBtn)
            okBtn.Click += (_, __) => dlg.Close();

        await dlg.ShowDialog(this);
    }
    
    private async void ButtonSaveBypass_Click(object? s, RoutedEventArgs e)
    {
        // If you store this elsewhere, use that value instead:
        await SaveBypassAsync();
    }

    private async void ButtonLoadBypass_Click(object? s, RoutedEventArgs e)
    {
        await LoadBypassAsync();
    }
    
    private static BypassLayoutFile ExportBypass(ArraySpec array)
    {
        var file = new BypassLayoutFile { };
        for (int s = 0; s < array.Strings.Count; s++)
        {
            var str = array.Strings[s];
            var entry = new BypassStringEntry { StringIndex = s };
            foreach (var d in str.BypassDiodes)
            {
                entry.Diodes.Add(new[] { d.CellIxs.First, d.CellIxs.Second });
            }
            file.Strings.Add(entry);
        }
        return file;
    }

    private static void ImportBypass(ArraySpec array, BypassLayoutFile file)
    {
        // Bounds-safe: clear & set only what the file provides
        foreach (var entry in file.Strings)
        {
            if (entry.StringIndex < 0 || entry.StringIndex >= array.Strings.Count) continue;
            var str = array.Strings[entry.StringIndex];
            str.BypassDiodes.Clear();

            int maxIx = str.Cells.Count - 1;
            foreach (var pair in entry.Diodes)
            {
                if (pair is { Length: 2 })
                {
                    int a = pair[0], b = pair[1];
                    if (a < 0 || b < 0 || a > b || a > maxIx || b > maxIx) continue;
                    str.BypassDiodes.Add(new ArraySpec.BypassDiode { CellIxs = new Pair<int>(a, b) });
                }
            }
        }

        // If you track forward drop on ArraySpec, set it here:
        // array.BypassDiodeSpec.VoltageDrop = file.ForwardDropVolts;
    }
    
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // TODO: wire this to a “Save Bypass…” button/menu
    private async System.Threading.Tasks.Task SaveBypassAsync()
    {
        // Export current in-memory config (what the control edited)
        var fileModel = ExportBypass(array);

        var sfd = new SaveFileDialog
        {
            Filters = { new FileDialogFilter { Name = "JSON", Extensions = { "json" } } },
            InitialFileName = "bypass_diodes.json"
        };
        var path = await sfd.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var json = JsonSerializer.Serialize(fileModel, _jsonOpts);
            await File.WriteAllTextAsync(path, json);
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Could not save JSON:\n{ex.Message}");
        }
    }
    
    private async System.Threading.Tasks.Task LoadBypassAsync()
    {
        var ofd = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters = { new FileDialogFilter { Name = "JSON", Extensions = { "json" } } }
        };
        var result = await ofd.ShowAsync(this);
        if (result is null || result.Length == 0) return;

        try
        {
            var text = await File.ReadAllTextAsync(result[0]);
            var fileModel = JsonSerializer.Deserialize<BypassLayoutFile>(text, _jsonOpts);
            if (fileModel is null) throw new InvalidOperationException("Empty or invalid JSON.");

            ImportBypass(array, fileModel);

            if (SelectedCellString is null && array.Strings.Count > 0)
                SelectedCellString = array.Strings[0];
            
            // refresh UI list so labels recompute immediately
            RefreshStringList();

            ArrayLayoutControl.Array = array;                   // reassign to trigger its setter
            ArrayLayoutControl.CellString = SelectedCellString; // re-apply selection
            ArrayLayoutControl.InvalidateVisual();              // belt-and-suspenders repaint

            UpdateView(); // (optional) updates other UI pieces
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Could not load JSON:\n{ex.Message}");
        }
    }
    
    private void RefreshStringList()
    {
        // Re-sync the UI collection from the model; this triggers a full rebind
        Strings.Clear();
        foreach (var s in array.Strings)
            Strings.Add(s);
    }
}
