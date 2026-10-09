using CommunityToolkit.Mvvm.ComponentModel;

namespace VoiceGrid.Models;

public partial class MeasurementRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double? _value1;
    [ObservableProperty] private double? _value2;
    [ObservableProperty] private double? _value3;

    public void Set(int valueColumn, double? v)
    {
        switch (valueColumn)
        {
            case 0: Value1 = v; break;
            case 1: Value2 = v; break;
            default: Value3 = v; break;
        }
    }
}
