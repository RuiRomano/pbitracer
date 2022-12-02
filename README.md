
# Intro

[PBITracer.exe](./dist/PBITracer.exe) is a cmdline tool for a quick trace of Power BI Datasets using the XMLA Endpoint.

![image](https://user-images.githubusercontent.com/10808715/144629287-327e4749-4b52-4316-9033-e332a14c950b.png)

# How to start a trace?

```Shell
PBITracer.exe -s "XMLA Endpoint" -d "Dataset Name" -u "Username or Service Principal Id (app:id@tenantid)" -p "Password or Service Principal Secret" --events [Events to trace]
```

Example of trace to monitor queries:

```Shell
PBITracer.exe -s "powerbi://api.powerbi.com/v1.0/myorg/WorkspaceToTrace" -d "Dataset1" --events QueryEnd
```

Example of trace to profile a Dataset Refresh

```Shell
PBITracer.exe -s "powerbi://api.powerbi.com/v1.0/myorg/WorkspaceToTrace" -d "Dataset1" --events ProgressReportEnd JobGraph Error ProgressReportError ProgressReportCurrent
```


## Output

All the traces are saved as JSON files in the output path (configurable using the -ouput parameter):

![image](https://user-images.githubusercontent.com/10808715/144311219-e369348e-0a71-48a2-8dfa-7b64a2f0e071.png)


## Authentication

If you execute PBITracer.exe without username (-u) and password (-p) a popup authentication will appear:

![image](https://user-images.githubusercontent.com/10808715/144308517-9eaa2424-6975-411e-ae1d-924a6ccf4fa0.png)

For non-interactive scenarios you can use a Service Principal, the parameters should have the following notation:

```Shell
-u app:[Service Principal Id]@[Tenant Id]
-p [Service Principal Secret]
```

Ensure the service principal is authorized on the following [Power BI Tenant Settings](https://docs.microsoft.com/en-us/power-bi/guidance/admin-tenant-settings):

- Allow service principals to user Power BI APIs
- Allow XMLA endpoints and Analyze in Excel with on-premises datasets

## Parameters

![image](https://user-images.githubusercontent.com/10808715/144629408-70008fb8-3c02-48b5-9152-adf68487737d.png)

# Sample Power BI Templates

## PBI Dataset Refresh Analysis

This template is an adaptation from [Phil Seamark PBIX](https://dax.tips/2021/02/15/visualise-your-power-bi-refresh/) solution.

Download the [PBI Dataset Refresh Analysis.pbit](./pbit/PBI%20Dataset%20Refresh%20Analysis.pbit) template and setup the location parameter to the output folder of PBITracer.exe and you should be able to analyze your dataset refresh operations:

![image](./images/Report1.png)
![image](./images/Report2.png)




