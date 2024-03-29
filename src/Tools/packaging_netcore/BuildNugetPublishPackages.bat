SET OUTDIR=C:\Git\ServiceConnect-CSharp\src\
SET OUTDIRFILTERS=C:\Git\ServiceConnect-CSharp\filters\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

:: comment

::NuGet pack "%OUTDIR%ServiceConnect\ServiceConnect.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Client.RabbitMQ\ServiceConnect.Client.RabbitMQ.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Interfaces\ServiceConnect.Interfaces.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Container.StructureMap\ServiceConnect.Container.StructureMap.nuspec
::NuGet pack "%OUTDIR%ServiceConnect.Container.ServiceCollection\ServiceConnect.Container.ServiceCollection.nuspec
::NuGet pack "%OUTDIR%ServiceConnect.Persistance.MongoDb\ServiceConnect.Persistance.MongoDb.nuspec
NuGet pack "%OUTDIR%ServiceConnect.Persistance.MongoDbSsl\ServiceConnect.Persistance.MongoDbSsl.nuspec
::NuGet pack "%OUTDIRFILTERS%ServiceConnect.Filters.MessageDeduplication\ServiceConnect.Filters.MessageDeduplication\ServiceConnect.Filters.MessageDeduplication.nuspec"
::NuGet pack "%OUTDIRFILTERS%ServiceConnect.Filters.GzipCompression\ServiceConnect.Filters.GzipCompression\ServiceConnect.Filters.GzipCompression.nuspec"

::nuget push ServiceConnect.5.0.17.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Client.RabbitMQ.5.0.11.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Interfaces.5.0.6.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Container.StructureMap.5.0.1.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Container.ServiceCollection.1.0.4.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Persistance.MongoDb.5.0.2.nupkg -Source https://www.nuget.org/api/v2/package
nuget push ServiceConnect.Persistance.MongoDbSsl.6.0.3.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Filters.MessageDeduplication.2.0.6.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Filters.GzipCompression.2.0.0-pre.nupkg -Source https://www.nuget.org/api/v2/package


@ECHO === === === === === === === ===

PAUSE
