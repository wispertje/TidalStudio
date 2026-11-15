using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace TidalStudio.App.Visualizers;

/// <summary>Builds an editor panel for a visualizer's options.</summary>
public static class OptionsPanel
{
    public static Control Build(OptionValues values)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,180"),
            Margin = new Thickness(4),
            MinWidth = 300,
        };
        int row = 0;
        foreach (var def in values.Definitions)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock
            {
                Text = def.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 12, 3),
                FontSize = 12,
            };
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            Control editor = def.Kind switch
            {
                OptionKind.Bool => BoolEditor(values, def),
                OptionKind.Number => NumberEditor(values, def),
                OptionKind.Choice => ChoiceEditor(values, def),
                _ => ColorEditor(values, def),
            };
            editor.Margin = new Thickness(0, 3);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
            row++;
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var reset = new Button { Content = "Reset to defaults", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) =>
        {
            foreach (var def in values.Definitions) values.Set(def.Key, def.Default);
            if (grid.Parent is ContentControl host) host.Content = Build(values);
            else if (grid.Parent is Decorator deco) deco.Child = Build(values);
        };
        Grid.SetRow(reset, row);
        Grid.SetColumnSpan(reset, 2);
        grid.Children.Add(reset);
        return grid;
    }

    private static Control BoolEditor(OptionValues values, VisOption def)
    {
        var cb = new CheckBox { IsChecked = values.Bool(def.Key), MinHeight = 0 };
        cb.IsCheckedChanged += (_, _) => values.Set(def.Key, cb.IsChecked == true ? "true" : "false");
        return cb;
    }

    private static Control NumberEditor(OptionValues values, VisOption def)
    {
        var text = new TextBlock { Width = 44, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextAlignment = TextAlignment.Right };
        var slider = new Slider
        {
            Minimum = def.Min,
            Maximum = def.Max,
            SmallChange = def.Step,
            LargeChange = def.Step * 5,
            TickFrequency = def.Step,
            IsSnapToTickEnabled = true,
            Value = values.Number(def.Key),
            Width = 130,
            MinHeight = 0,
            Margin = new Thickness(0, -6),
        };
        void Show() => text.Text = slider.Value.ToString(def.Step >= 1 ? "0" : def.Step >= 0.1 ? "0.0#" : "0.00#", CultureInfo.InvariantCulture);
        Show();
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            values.Set(def.Key, slider.Value.ToString(CultureInfo.InvariantCulture));
            Show();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(slider);
        panel.Children.Add(text);
        return panel;
    }

    private static Control ChoiceEditor(OptionValues values, VisOption def)
    {
        var combo = new ComboBox { ItemsSource = def.Choices, SelectedItem = values.Choice(def.Key), MinWidth = 120, FontSize = 12 };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) values.Set(def.Key, s); };
        return combo;
    }

    private static Control ColorEditor(OptionValues values, VisOption def)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox { Text = values.Get(def.Key), Width = 110, FontSize = 12, MinHeight = 0 };
        void Update()
        {
            if (Color.TryParse(box.Text, out var c))
            {
                swatch.Background = new SolidColorBrush(c);
                values.Set(def.Key, box.Text!.Trim());
            }
        }
        Update();
        box.TextChanged += (_, _) => Update();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(swatch);
        panel.Children.Add(box);
        return panel;
    }
}
