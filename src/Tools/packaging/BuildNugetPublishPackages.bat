SET OUTDIR=C:\GitHub\R.MessageBus\src\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

NuGet pack "%OUTDIR%R.MessageBus.Client.RabbitMQ\R.MessageBus.Client.RabbitMQ.nuspec"
NuGet pack "%OUTDIR%R.MessageBus\R.MessageBus.nuspec"
NuGet pack "%OUTDIR%R.MessageBus.Interfaces\R.MessageBus.Interfaces.nuspec"


nuget push R.MessageBus.0.0.0.5.nupkg
           
@ECHO === === === === === === === ===

PAUSE
