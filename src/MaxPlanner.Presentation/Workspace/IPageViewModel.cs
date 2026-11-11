using System.ComponentModel;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>A retained page ViewModel (design §2.2: page ViewModels are kept for the session).</summary>
public interface IPageViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>The status-bar page summary (design §2.8).</summary>
    string Summary { get; }

    /// <summary>
    /// The page's data-region state (design §2.5); Loading until its first data arrived. A page without one is
    /// always Populated. The shell's page preload waits for it before it creates the page view.
    /// </summary>
    ViewState State => ViewState.Populated;
}
