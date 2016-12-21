SET OUTDIR=C:\Git\ServiceConnect\src\
SET OUTDIRFILTERS=C:\Git\ServiceConnect\filters\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

:: comment

NuGet pack "%OUTDIR%ServiceConnect\ServiceConnect.nuspec"
NuGet pack "%OUTDIR%ServiceConnect.Client.RabbitMQ\ServiceConnect.Client.RabbitMQ.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Interfaces\ServiceConnect.Interfaces.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Container.StructureMap\ServiceConnect.Container.StructureMap.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Persistance.MongoDb\ServiceConnect.Persistance.MongoDb.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Persistance.MongoDbSsl\ServiceConnect.Persistance.MongoDbSsl.nuspec"
::NuGet pack "%OUTDIR%ServiceConnect.Container.Ninject\ServiceConnect.Container.Ninject.nuspec"
::NuGet pack "%OUTDIRFILTERS%ServiceConnect.Filters.MessageDeduplication\ServiceConnect.Filters.MessageDeduplication\ServiceConnect.Filters.MessageDeduplication.nuspec"


nuget push ServiceConnect.3.1.11-pre.nupkg -Source https://www.nuget.org/api/v2/package
nuget push ServiceConnect.Client.RabbitMQ.3.1.10-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Interfaces.3.1.3-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Container.StructureMap.3.1.5-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Persistance.MongoDb.3.1.3-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Persistance.MongoDbSsl.3.1.3-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Container.Ninject.3.1.5-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Filters.MessageDeduplication.1.0.3-pre.nupkg -Source https://www.nuget.org/api/v2/package        

@ECHO === === === === === === === ===

PAUSE
