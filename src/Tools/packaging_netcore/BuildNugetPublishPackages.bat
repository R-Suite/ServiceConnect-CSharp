SET OUTDIR=C:\Git\ServiceConnect\src\
SET OUTDIRFILTERS=C:\Git\ServiceConnect\filters\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

:: comment

::NuGet pack "%OUTDIR%ServiceConnect\ServiceConnect.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Client.RabbitMQ\ServiceConnect.Client.RabbitMQ.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Interfaces\ServiceConnect.Interfaces.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Container.StructureMap\ServiceConnect.Container.StructureMap.nuspec
::NuGet pack "%OUTDIR%ServiceConnect.Persistance.MongoDb\ServiceConnect.Persistance.MongoDb.nuspec
::NuGet pack "%OUTDIR%ServiceConnect.Persistance.MongoDbSsl\ServiceConnect.Persistance.MongoDbSsl.nuspec
NuGet pack "%OUTDIRFILTERS%ServiceConnect.Filters.MessageDeduplication\ServiceConnect.Filters.MessageDeduplication\ServiceConnect.Filters.MessageDeduplication.nuspec"
::NuGet pack "%OUTDIRFILTERS%ServiceConnect.Filters.GzipCompression\ServiceConnect.Filters.GzipCompression\ServiceConnect.Filters.GzipCompression.nuspec"

::nuget push ServiceConnect.4.0.2-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Client.RabbitMQ.4.0.1-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Interfaces.4.0.0-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Container.StructureMap.4.0.3-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Persistance.MongoDb.4.0.2-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Persistance.MongoDbSsl.4.0.3-pre.nupkg -Source https://www.nuget.org/api/v2/package
nuget push ServiceConnect.Filters.MessageDeduplication.2.0.4-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Filters.GzipCompression.2.0.0-pre.nupkg -Source https://www.nuget.org/api/v2/package


@ECHO === === === === === === === ===

PAUSE
