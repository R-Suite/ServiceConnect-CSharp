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
::NuGet pack "%OUTDIR%ServiceConnect.Container.Ninject\ServiceConnect.Container.Ninject.nuspec"


nuget push ServiceConnect.2.1.13-beta.nupkg
nuget push ServiceConnect.Client.RabbitMQ.2.1.7-beta.nupkg
::nuget push ServiceConnect.Interfaces.2.1.3-beta.nupkg
::nuget push ServiceConnect.Container.StructureMap.2.1.9-beta.nupkg
::nuget push ServiceConnect.Persistance.MongoDb.2.1.0-beta.nupkg
::nuget push ServiceConnect.Container.Ninject.2.1.4-beta.nupkg           

@ECHO === === === === === === === ===

PAUSE
