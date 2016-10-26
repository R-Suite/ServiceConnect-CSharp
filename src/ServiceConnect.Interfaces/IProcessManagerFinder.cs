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

using System;

namespace ServiceConnect.Interfaces
{
    public delegate void TimeoutInsertedDelegate(DateTime timeoutTime);

    public interface IProcessManagerFinder
    {
        event TimeoutInsertedDelegate TimeoutInserted;

        IPersistanceData<T> FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData;

        void InsertData(IProcessManagerData data);
        void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData;
        void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData;

        void InsertTimeout(TimeoutData timeoutData);
        TimeoutsBatch GetTimeoutsBatch();
        void RemoveDispatchedTimeout(Guid id);
    }
}
