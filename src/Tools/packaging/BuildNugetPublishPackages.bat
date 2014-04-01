SET OUTDIR=C:\GitHub\R.MessageBus\src\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

NuGet pack "%OUTDIR%R.MessageBus.Client.RabbitMQ\R.MessageBus.Client.RabbitMQ.nuspec"
NuGet pack "%OUTDIR%R.MessageBus\R.MessageBus.nuspec"

nuget push R.MessageBus.0.0.0.3.nupkg
nuget push R.MessageBus.Client.RabbitMQ.0.0.0.3.nupkg
           
@ECHO === === === === === === === ===

PAUSE
