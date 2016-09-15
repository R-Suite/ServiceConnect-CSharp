//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Container.Default;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.SqlServer;
using Xunit;

namespace ServiceConnect.IntegrationTests.Bus
{
    public class BusSetupTests
    {
        [Fact]
        public void ShouldSetupBusWithDefaultConfiguration()
        {
            // Arrange / Act
            IBus bus = ServiceConnect.Bus.Initialize();

            // Assert
            Assert.Equal(typeof(Consumer), bus.Configuration.ConsumerType);
            Assert.Equal(typeof(Producer), bus.Configuration.ProducerType);
            Assert.Same(typeof(DefaultBusContainer), bus.Configuration.GetContainer().GetType());
            Assert.Equal(typeof(SqlServerProcessManagerFinder), bus.Configuration.ProcessManagerFinder);

            bus.StopConsuming();
            bus.Dispose();
        }
    }
}
