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

using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.IntegrationTests.Bus
{
    public class BusSetupTests
    {
        public class TestHandler : IMessageHandler<Message>
        {
            public IConsumeContext? Context { get; set; }
            public Task HandleAsync(Message message)
            {
                throw new System.NotImplementedException();
            }
        }

        [Fact]
        public void ShouldSetupBusViaServiceCollection()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddServiceConnect(_ => { });

            // Act
            var provider = services.BuildServiceProvider();
            var bus = provider.GetRequiredService<IBus>();

            // Assert
            Assert.NotNull(bus);
        }
    }
}
