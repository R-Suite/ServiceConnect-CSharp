// Copyright (C) 2015 Timothy Watson, Jakub Pachansky

// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301, USA.

namespace ServiceConnect.Interfaces
{
    public static class HeaderKeys
    {
        public const string MessageType = "MessageType";
        public const string FullTypeName = "FullTypeName";
        public const string TypeName = "TypeName";
        public const string RoutingKey = "RoutingKey";
        public const string SourceAddress = "SourceAddress";
        public const string RequestMessageId = "RequestMessageId";
        public const string ResponseMessageId = "ResponseMessageId";
        public const string CorrelationId = "CorrelationId";
        public const string MessageId = "MessageId";
        public const string Redelivered = "Redelivered";
        public const string DestinationAddress = "DestinationAddress";
        public const string Publish = "Publish";
        public const string RoutingSlip = "RoutingSlip";
        public const string SequenceId = "SequenceId";
        public const string PacketNumber = "PacketNumber";
        public const string LastPacketNumber = "LastPacketNumber";
    }
}