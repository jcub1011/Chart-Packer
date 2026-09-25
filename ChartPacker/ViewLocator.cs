using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ChartPacker.ViewModels;
using ChartPacker.Views;

namespace ChartPacker
{
    /// <summary>
    /// Given a view model, returns the corresponding view. The mapping is explicit so no
    /// reflection is needed under Native AOT; add a case here for each new view model.
    /// </summary>
    public class ViewLocator : IDataTemplate
    {
        public Control? Build(object? param) => param switch
        {
            null => null,
            LibraryViewModel => new LibraryView(),
            SongDetailViewModel => new SongDetailView(),
            _ => new TextBlock { Text = "No view registered for " + param.GetType().Name },
        };

        public bool Match(object? data)
        {
            return data is ViewModelBase;
        }
    }
}
