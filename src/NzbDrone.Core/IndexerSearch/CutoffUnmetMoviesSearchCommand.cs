using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class CutoffUnmetMoviesSearchCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override bool IsSearchCommand => true;
        public string FilterKey { get; set; }
        public string FilterValue { get; set; }
    }
}
