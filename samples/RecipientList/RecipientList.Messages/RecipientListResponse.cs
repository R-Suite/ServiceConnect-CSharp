﻿using System;
using ServiceConnect.Interfaces;

namespace RecipientList.Messages
{
    public class RecipientListResponse : Message
    {
        public RecipientListResponse(Guid correlationId) : base(correlationId) { }

        public string Endpoint { get; set; }
    }
}