SET OUTDIR=C:\Git\ServiceConnect\src\

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


nuget push ServiceConnect.3.1.2-pre.nupkg -Source https://www.nuget.org/api/v2/package
nuget push ServiceConnect.Client.RabbitMQ.3.1.1-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Interfaces.3.1.0-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Container.StructureMap.3.1.0-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Persistance.MongoDb.3.1.0-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Persistance.MongoDbSsl.3.1.0-pre.nupkg -Source https://www.nuget.org/api/v2/package
::nuget push ServiceConnect.Container.Ninject.3.1.0-pre.nupkg -Source https://www.nuget.org/api/v2/package          

@ECHO === === === === === === === ===

PAUSE
