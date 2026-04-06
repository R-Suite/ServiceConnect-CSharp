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

using System;
using ServiceConnect.Core;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class RoutingKeyTests
    {
        [Fact]
        public void ShouldStoreValue()
        {
            // Arrange & Act
            var routingKey = new RoutingKey("test-routing-key");

            // Assert
            Assert.Equal("test-routing-key", routingKey.GetValue());
        }

        [Fact]
        public void ShouldAllowMultipleInstances()
        {
            // Arrange & Act
            var routingKey1 = new RoutingKey("key1");
            var routingKey2 = new RoutingKey("key2");

            // Assert
            Assert.Equal("key1", routingKey1.GetValue());
            Assert.Equal("key2", routingKey2.GetValue());
        }

        [Fact]
        public void ShouldHandleEmptyString()
        {
            // Arrange & Act
            var routingKey = new RoutingKey("");

            // Assert
            Assert.Equal("", routingKey.GetValue());
        }

        [Fact]
        public void ShouldHandleNullString()
        {
            // Arrange & Act
            var routingKey = new RoutingKey(null);

            // Assert
            Assert.Null(routingKey.GetValue());
        }

        [Fact]
        public void ShouldAllowMultipleAttributesOnClass()
        {
            // Arrange
            var attributes = typeof(TestRoutingKeyClassMultiple).GetCustomAttributes(typeof(RoutingKey), true);

            // Assert
            Assert.Equal(2, attributes.Length);
        }
    }

    [RoutingKey("test-key")]
    class TestRoutingKeyClass { }

    [RoutingKey("key1")]
    [RoutingKey("key2")]
    class TestRoutingKeyClassMultiple { }
}