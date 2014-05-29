SET OUTDIR=C:\GitHub\R.MessageBus\src\

@ECHO === === === === === === === ===

@ECHO ===NUGET Publishing ....

del *.nupkg

:: NuGet pack "%OUTDIR%R.MessageBus.Client.RabbitMQ\R.MessageBus.Client.RabbitMQ.nuspec"
NuGet pack "%OUTDIR%R.MessageBus\R.MessageBus.nuspec"
:: NuGet pack "%OUTDIR%R.MessageBus.Interfaces\R.MessageBus.Interfaces.nuspec"


nuget push R.MessageBus.1.0.0.12.nupkg
:: nuget push R.MessageBus.Interfaces.1.0.0.11.nupkg
:: nuget push R.MessageBus.Client.RabbitMQ.1.0.0.11.nupkg
           
@ECHO === === === === === === === ===

PAUSE
