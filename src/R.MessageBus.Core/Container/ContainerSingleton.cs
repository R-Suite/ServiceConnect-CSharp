namespace R.MessageBus.Core.Container
{
    public class ContainerSingleton
    {
        public static readonly Container _instance = new Container();

        private ContainerSingleton()
        {
        }
    }
}
