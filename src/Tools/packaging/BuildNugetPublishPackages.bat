SET OUTDIR=C:\GitHub\R.MessageBus\src\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

:: comment

NuGet pack "%OUTDIR%R.MessageBus.Client.RabbitMQ\R.MessageBus.Client.RabbitMQ.nuspec"
NuGet pack "%OUTDIR%R.MessageBus\R.MessageBus.nuspec"
NuGet pack "%OUTDIR%R.MessageBus.Interfaces\R.MessageBus.Interfaces.nuspec"
NuGet pack "%OUTDIR%R.MessageBus.Persistance.MongoDb\R.MessageBus.Persistance.MongoDb.nuspec"


nuget push R.MessageBus.1.1.13-beta.nupkg
nuget push R.MessageBus.Interfaces.1.1.13-beta.nupkg
nuget push R.MessageBus.Client.RabbitMQ.1.1.13-beta.nupkg
nuget push R.MessageBus.Persistance.MongoDb.1.1.13-beta.nupkg
           
@ECHO === === === === === === === ===

PAUSE
