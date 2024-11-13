Developer Notes for DivisiBill Web Service
==========================================

This is a fairly random set of comments on topics of interest to DivisiBill 
web service developers with the most important ones for initial development at the start.

Building the Solution
---------------------

You should be able to clone the repository and simply build and run the solution locally where it will run
on an Azure emulation environment. However
its functionality will be very limited until it is provided with credentials for the Google play Store
for license checking and Azure Cognitive Services for the bill OCR implementation. If you run the web service in debug mode locally using an emulated Azure environment you can access the web service and use emulated table storage without 
needing an Azure key, license checking still requires a set of Google credentials usable with the Play Store (see below). To deploy it in production you'll need to define an Azure Function service.

To get a fully functional version you'll need to define a number of secrets in environment variables.
In Azure they come from the secrets store and YAML maps the secrets into environment variables.
Locally they are simple defined as persistent environment
variables, ether per-user or per-machine (see the DOS SETX command or read "Google Authentication Secret" below for PowerShell). Here's a summary of them:

| Environment variable         | Usage |
|
| DIVISIBILL_SENTRY_DSN        | The path to the Sentry application health service
| SENTRY_AUTH_TOKEN            | The authentication token for the Sentry application health service
| DIVISIBILL_PLAY_CREDENTIALS  | Credentials for access to the Google Play Store
| DIVISIBILL_WS_CS_EP          | Azure Form recognizer Endpoint
| DIVISIBILL_WS_CS_KEY         | Azure Form recognizer Key

Read through the section on "Build Information and Secrets" and "Automated Build and Deployment" 
below for more information, especially on how and why we use Base-64 encoding and the mechanism 
by which this stuff is made available at runtime.

Local Files
-----------

Really, properties\launchsettings.json and local.settings.json ought not to be in the repository because they
may contain secrets. Still, versions of those files are checked in just to make life easier for someone who 
wants to to try out the code.

Branching strategy
------------------

This is essentially identical to that of DivisiBill itself so read the DivisiBill developer notes for details 
of that.

The main difference is that the CI/CD deployment phase deploys to Azure rather than the play store and it builds
from a stream called "release". The release definition is in azure-functions-app-release-alternate.yml and what it does is deploy the new web service to the alternate deployment slot for the Azure DivisiBillWs .

Testing
=======

The web service can be tested using the DivisiBill app by setting the DIVISIBILL_WS_URI environment variable to the URL for the
web service, no key is needed. This setup must be done before running the app. The easiest way to do this and have the environment
variable persist is to start a command prompt and enter:

>    SETX DIVISIBILL_WS_URI http://localhost:7190/api/

Don't forget to restart the Visual Studio for DivisiBill after doing this so the new environment variable can be used by the build process.

When testing on another host (like an Android phone or emulator) it's tricky because the local debug web service only offers a local URL.
The good news is that there are a couple of solutions that can take such an address and offer it on the Internet as well 
as adding monitoring capabilities. Once there's a globally accessible URL for the web service you can plug it 
into the client app (DivisiBill) by setting an environment variable on the build machine using something like

>    SETX DIVISIBILL_WS_URI https://xxxxxx.usw3.devtunnels.ms/api/

An all Microsoft solution is to use the 'dev tunnels' feature introduced in Visual Studio 2022, that's what the URL above is for. 
This is pretty simple to do, first define a persistent public tunnel then use the dev tunnels window 
to view the tunnel URL. Then define this as the tunnel to use when initiating the web service in debug mode by
setting an environment variable as above.

There is also a third party product called 'ngrok'. The 'dev tunnels' solution is simpler to setup and use but less fully featured and as of early 2024 still a bit buggy.

You can request ngrok use persistent domain names which saves time because instead of generating
a new domain name each time it is run ngrok always uses the same one. You get to register one name for free
and it will typically be of of the form "some-random-words.ngrok-free.app".

You just run ngrok specifying the name, in our example:
>     c:\util\ngrok http --domain=some-random-words.ngrok-free.app 7190

Alternatively, use a predefined configuration from the ngrok configuration file (below) by running it with
>     c:\util\ngrok.exe start divisibillws
The app will display relevant information, especially a URL that can be used to reach the local service.

Occasionally it will fail with something like:
>  ERROR:  listen tcp 127.0.0.1:4049: bind: An attempt was made to access a socket in a way forbidden by its access permissions.

Ostensibly this is because port 4049 (in the example above) is already in use but sometimes it is not and the problem is with TCP reserved ports which you can enumerate using:
>    netsh interface ipv4 show excludedportrange protocol=tcp

This may reveal the port you wanted to use (4049 in the example above) is reserved. This may be because dynamic ports are set to a low value, see:
>    netsh int ipv4 show dynamicport tcp

If the answer is 1025 try setting it higher:
>    netsh int ipv4 set dynamic tcp start=49152 num=16384

Then reboot (this hint came from https://superuser.com/questions/1579346/many-excludedportranges-how-to-delete-hyper-v-is-disabled)

If you're recreating the ngrok configuration it's typically at %LOCALAPPDATA%\ngrok\ngrok.yml and looks like this:
```
    version: "2"
    region: us
    authtoken: <string of 50 or so characters>
    tunnels:
        divisibillws:
            proto: http
            domain: some-random-words.ngrok-free.app
            addr: 7190
            host_header: localhost
```
The authtoken is specific to a registered user (registration is free) go to 
https://dashboard.ngrok.com the port number is from properties\launchsettings.json in the DivisiBillWs project.

License Checking Functions
==========================

Two kinds of licenses are used:

- a 'pro' subscription which may be renewed annually and grants access to cloud storage
- an 'OCR' license which grants the right to scan 30 bills (the user can purchase another when the count of remaining scans drops below 5).

The "recordpurchase" web service checks that a purchase is from the store (just the Google Play store for now), that
it is an unacknowledged (new) purchase, and that it has not previously been recorded. If it passes those tests, the purchase Order ID and the product ID it was for are recorded 
along with an initial credit of scans if it is an OCR purchase. The calling program should then acknowledge the purchase with the store so that it is now an acknowledged purchase (the Play Store discards unacknowledged ones after a few minutes).

The 'verify' web service checks licenses in two ways, first it checks that the license was issued by Google
(because only Android licenses work right now) and has been acknowledged, then it checks whether this license has been
seen before. If any of those tests fail we return an error, otherwise we look up information associated with
the license (the number of remaining OCR scans on that license) and return the information we found.

All functions need authority to call Google and authorization to create and use one or more Azure table. The Azure
authorization comes in the form of a connection string which Azure provides as part of the web app configuration
in a value called "AzureWebJobsStorage". See below for more information on Google Authentication.

Image Scanning Function
=======================

The image scanning function takes a POST message containing a JSON encoded license and a JPG image of a bill.
It validates the license, ensures it has at least one scan left and calls an Azure Form Recognizer (formerly 
Cognitive Services) web service passing a key. If the scan does not fail it decrements the remaining scan count 
for that license and returns the scan results and the remaining scan count.

Google Authentication 
=====================

Essentially, Google authentication requires a "Service Account" defined in https://console.cloud.google.com which will produce
a JSON file suitable for use in authenticating a web service wanting to act as that account. The complexity comes in that the Google account
must also be referenced from the play store at https://console.play.google.com and of course all the permissions have to be right.
A good description of this process exists for the "revenuecat" product, which has to solve much the same problem, see this at
https://www.revenuecat.com/docs/creating-play-service-credentials - in essence you point the Google Play Console to a 
Google Cloud Project, then create a service account with the right roles in that project. After that you can switch back to 
the play console and give the service account permissions in that app. Alas you then have to wait many hours (12 worked for me)
before it all can be used. To use it your app consumes the JSON credentials and voilà it's allowed to call the requisite 
functions, tedious but it eventually works.

Once you have got a good account you'll need to save a key for it in JSON form so it can be used at run time to
authenticate to the Google Play Store and check licenses. Encoding a multi-line JSON file in an environment variable so it can be
used at run time isn't trivial, see "Google Authentication Secret" below for more details on how to do that.

Azurite Emulator
================

This is used to provide local copies of the Azure Tables the functions app uses, normally VS will initiate this using the following command (shown
as 3 lines but it's really just one):
```
   c:\program files\microsoft visual studio\2022\preview\common7\ide\extensions\microsoft\Azure Storage Emulator\azurite.exe 
      --location "C:\Users\david\AppData\Local\.vstools\azurite" 
      --debug "C:\Users\david\AppData\Local\.vstools\azurite\debug.log" --skipApiVersionCheck
```
That means you don't normally need to do anything, just load up the DivisiBillWs project into VS and go.

CLI Commands to Run the Functions Locally
=========================================

There's an emulator that will allow the functions code to be debugged locally as long as the requisite Azure Functions Core
Tools CLI is installed. use it from the root of the project, so something like:

    CD \Documents\Develop\Phone\DivisiBillWs\DivisiBillWs\
    func start

That should initialize the functions environment listening on a port it will display.

Command File to Run a Local Debug Version
=========================================

Once everything is set up there's a solution level file RunWs.cmd that will run Azurite, ngrok and the web service. This
is handy for debugging DivisiBill and letting the web service take care of itself.

GitHub Actions
==============

The project has a GitHub action used to build and deploy the web
service in response to pushing the appropriate stream (release).
Look in azure-functions-app-release-alternate.yml for details.

Build Information and Secrets
-----------------------------

There's information that belongs in the build process, not in the source. There's also some 
secret information (like the web service key) that doesn't belong in source control. The solution 
to both these problems is project file tasks that generate files containing the required information.

Most of  the generated files are created in the $(IntermediateOutputPath) folder.

For example, the BuildInfo.cs file will look something like this

    namespace DivisiBillWs.Generated;
    
    internal class BuildInfo
    {
        internal const string DivisiBillSentryDsn = "https://335...816.ingest.us.sentry.io/4...4";
    }

On the developer machine, the secrets are stored in environment variables that mostly begin with DIVISIBILL_.
CI/CD creates environment variables on the fly using secrets stored in the CI/CD system. Secrets are 
intentionally named the same as the corresponding environment variable if there is one. The list is:

| Secret                       | Usage 
|
| DIVISIBILL_SENTRY_DSN        | The path to the Sentry application health service
| SENTRY_AUTH_TOKEN            | The authentication token for the Sentry application health service
| DIVISIBILL_PLAY_CREDENTIALS  | Credentials for access to the Google Play Store
| DIVISIBILL_WS_CS_EP          | Azure Form recognizer Endpoint
| DIVISIBILL_WS_CS_KEY         | Azure Form recognizer Key

There's also one secret used for deployment: AZURE_FUNCTIONAPP_PUBLISH_PROFILE_RELEASE_ALTERNATE
It has no environment variable counterpart.

The secrets that need to be available at run time are processed into constants by the build. For example: 

- DivisiBillSentryDsn / DIVISIBILL_SENTRY_DSN - The URI used to reach the Sentry web service. Get this from the definition on the Sentry web site.

Build Time
----------

The build time displayed by the app is generated by a similar mechanism but it generates a class called 
BuildEnvironment.

When you load the project into Visual Studio it will generate both files so you can take a look at them if you want - just remember not to change them since they'll be regenerated on every build and your changes will be lost. If you do need them to be different edit the templates in the project file.

Secrets Using GitHub CLI
------------------------

You can set up many of the secrets using the environment variables you already set, a git command prompt, and the gitHub 
CLI, for example:
```
gh secret set DIVISIBILL_SENTRY_DSN       -b "%DIVISIBILL_SENTRY_DSN%"
gh secret set DIVISIBILL_PLAY_CREDENTIALS -b "%DIVISIBILL_PLAY_CREDENTIALS%"
gh secret set DIVISIBILL_WS_CS_EP         -b "%DIVISIBILL_WS_CS_EP%"
gh secret set DIVISIBILL_WS_CS_KEY        -b "%DIVISIBILL_WS_CS_KEY%"
```
or, in PowerShell:
```
gh secret set DIVISIBILL_SENTRY_DSN       -b "$env:DIVISIBILL_SENTRY_DSN";
gh secret set DIVISIBILL_PLAY_CREDENTIALS -b "$env:DIVISIBILL_PLAY_CREDENTIALS";
gh secret set DIVISIBILL_WS_CS_EP         -b "$env:DIVISIBILL_WS_CS_EP";
gh secret set DIVISIBILL_WS_CS_KEY        -b "$env:DIVISIBILL_WS_CS_KEY";
```
The secret used for the build process is not in a local environment variable so you'll need to 
set it explicitly, typically from a file. That's easiest at a command prompt rather than in PowerShell:
```
gh secret set AZURE_FUNCTIONAPP_PUBLISH_PROFILE_RELEASE_ALTERNATE < publish.profile.xml;
```
or, in PowerShell:
```
gh Get-Content publish.profile.xml | secret set AZURE_FUNCTIONAPP_PUBLISH_PROFILE_RELEASE_ALTERNATE
```

Google Service Account
----------------------

During the build the service account described in "Google Credentials" above is retrieved from an environment variable
and deposited as a long string in the BuildInfo file along with other secrets. First though, you have to get it into
an environment variable. The one used is

> DIVISIBILL_PLAY_CREDENTIALS

Because the secret data is JSON it seems reasonable that it could just be stored directly in an environment variable but because it contains embedded carriage returns and special characters it's a pain to deal with so we first encode it as a base 64 string. Once you've got the JSON file above, for example "play.json" you can use PowerShell to copy it in base 64 into an environment variable like this:  
```
    # Read the original file containing the JSON encoded credential as a byte array
    $jsonArray = Get-Content "$PWD\PlayStore.json" -Encoding Byte -Raw

    # Convert the array to a string
    $b64DataString = [Convert]::ToBase64String($jsonArray)

    # Store the string as a default environment variable for this user (it will not be visible in this session)
    [Environment]::SetEnvironmentVariable("DIVISIBILL_PLAY_CREDENTIALS", $b64DataString, "User")
```
Note that to create a machine-wide environment variable an administrator can specify "Machine" in place of "User".

Once that is done **future** programs will be initiated with the new variable present but it will not be visible in the current session. For example you could see it in PowerShell by starting a new PowerShell prompt and entering:

> $env:DIVISIBILL_PLAY_CREDENTIALS

With the environment variable created the build will take care of the rest, reading the environment variable contents and turning them into a string. Converting them from base64 back to JSON is done at run time. If you are interested in doing this in PowerShell do it like this:
```
    $b64Decoded = [Convert]::FromBase64String($env:DIVISIBILL_PLAY_CREDENTIALS)

    [IO.File]::WriteAllBytes("$PWD\PlayStore.json",$b64Decoded)
```
If you want to put it in a string instead, use

> b64DecodedString = [System.Text.Encoding]::UTF8.GetString($b64Decoded)

Unfortunately this function is not available in MsBuild (at least as of .NET8) so you cannot do the conversion in the csproj file and just write the JSON to a string.

Azure Function Deployment Secret
--------------------------------

The last step of the CI/CD process is to deploy the newly built function app to Azure and to do this you need valid
credentials, Azure provides a "publish profile" for this purpose. The publish profile data is stored in a GitHub secret called AZURE_FUNCTIONAPP_PUBLISH_PROFILE_RELEASE_ALTERNATE.

<center>* * * * * * </center>