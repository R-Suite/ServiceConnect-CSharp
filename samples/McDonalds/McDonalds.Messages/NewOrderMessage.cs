namespace McDonalds.Messages
{
    using System;
    using R.MessageBus.Interfaces;

    namespace McDonalds.Messages
    {
        public class NewOrderMessage : Message
        {
            public string Name { get; set; }
            public string Size { get; set; }

            public NewOrderMessage(Guid correlationId)
                : base(correlationId)
            { }
        }
    }
}