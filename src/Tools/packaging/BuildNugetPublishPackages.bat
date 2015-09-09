SET OUTDIR=C:\Git\R.MessageBus\src\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

:: comment

NuGet pack "%OUTDIR%R.MessageBus.Client.RabbitMQ\R.MessageBus.Client.RabbitMQ.nuspec"
NuGet pack "%OUTDIR%R.MessageBus\R.MessageBus.nuspec"
NuGet pack "%OUTDIR%R.MessageBus.Interfaces\R.MessageBus.Interfaces.nuspec"
NuGet pack "%OUTDIR%R.MessageBus.Container.StructureMap\R.MessageBus.Container.StructureMap.nuspec"
NuGet pack "%OUTDIR%R.MessageBus.Persistance.MongoDb\R.MessageBus.Persistance.MongoDb.nuspec"
NuGet pack "%OUTDIR%R.MessageBus.Container.Ninject\R.MessageBus.Container.Ninject.nuspec"


nuget push R.MessageBus.2.1.9-beta.nupkg
nuget push R.MessageBus.Interfaces.2.1.3-beta.nupkg
nuget push R.MessageBus.Client.RabbitMQ.2.1.3-beta.nupkg
nuget push R.MessageBus.Container.StructureMap.2.1.9-beta.nupkg
nuget push R.MessageBus.Persistance.MongoDb.2.1.0-beta.nupkg
nuget push R.MessageBus.Container.Ninject.2.1.4-beta.nupkg           

@ECHO === === === === === === === ===

PAUSE
