using SCADA.Runtime.Runtime;

namespace SCADA.ViewModels;

public sealed class RuntimeViewModel : ViewModelBase
{
    public TagsViewModel TagsViewModel { get; }
    public SchemesViewModel SchemesViewModel { get; }
    public RuntimeHost Host { get; }

    public RuntimeViewModel(RuntimeHost host, TagsViewModel tagsViewModel, SchemesViewModel schemesViewModel)
    {
        Host = host;
        TagsViewModel = tagsViewModel;
        SchemesViewModel = schemesViewModel;
        SchemesViewModel.Canvas.StartLive();
    }
}
