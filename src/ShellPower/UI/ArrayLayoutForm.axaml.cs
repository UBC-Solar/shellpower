using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
}
