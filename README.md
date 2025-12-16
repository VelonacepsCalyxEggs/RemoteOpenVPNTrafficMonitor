# Remote OpenVPN Traffic Monitor

A background service that monitors OpenVPN client traffic statistics by connecting to an OpenVPN and Wireguard server via SSH, parsing traffic data, and storing throughput metrics in a PostgreSQL database.

## Features

- **SSH Integration**: Connects to OpenVPN server using SSH credentials
- **Traffic Monitoring**: Collects bytes in/out metrics from OpenVPN status information
- **Throughput Calculation**: Computes real-time network throughput for each client
- **Database Storage**: Stores traffic data in PostgreSQL with automatic database/table creation
- **Automatic Reconnection**: Handles SSH disconnections and automatically reconnects
- **Counter Reset Protection**: Detects and handles counter resets in OpenVPN statistics

## Prerequisites

- .NET 7.0 or later
- PostgreSQL database
- OpenVPN server with management interface enabled
- SSH access to the OpenVPN server

## Building the Application

1. Clone or download the project.
2. Navigate to the project directory in your terminal.
3. Build the project:
   ```bash
   dotnet build
   ```
4. (Optional) Publish the application:
   ```bash
   dotnet publish -c Release -o ./publish
   ```

## Configuration

1. Initialize the secrets manager:
   ```bash
   dotnet user-secrets init
   ```
   I did not yet implement an ability to use certs, so... uh... passwords only for now!
2. Set your secrets:
   ```bash
   "vpnServers": [
       {
         "Name": "Server 1",
         "Address": "x.x.x.x",
         "Type": 0,
         "Port": 22,
         "Username": "verycoolusername1",
         "Password": "verystrongpassword1",
         "PollingIntervalSeconds": 10
       },
       {
         "Name": "Server N",
         "Address": "x.x.x.x",
         "Type": 1,
         "Port": 22,
         "Username": "verycoolusername2",
         "Password": "verystrongpassword2",
         "PollingIntervalSeconds": 10
    }
     ],
     "dbConnectionString": "Host=x.x.x.x;Port=5432;Username=postgres;Password=veryverystrongpasswordorsomethingelse"
   ```

### Configuration Parameters

- **PollingIntervalSeconds**: Time in seconds between data collection cycles (minimum: 1)
You can (theoretically) have as many servers as you wish listed in secrets.
- **Type**: 0 for OpenVPN, 1 for WireGuard.
## Installation
- Configure your secrets or environment variables as described above.
- Run the service:
  ```bash
  dotnet run
  ```

## Database Setup

The application automatically:
- Checks for the existence of the `vpntraffic` database.
- Creates the database if it doesn't exist.
- Creates a `[SERVER NAME]` table with the following schema:

```sql
CREATE TABLE [SERVER NAME] (
    client_name VARCHAR(255) NOT NULL,
    ip_addr VARCHAR(15) NOT NULL,
    client_upload BIGINT NOT NULL CHECK (bytes_in >= 0),
    client_download BIGINT NOT NULL CHECK (bytes_out >= 0),
    measured_at TIMESTAMP DEFAULT NOW()
);
```

## How It Works

1. **SSH Connection**: Establishes an SSH connection to the OpenVPN server.
2. **Data Collection**: Retrieves OpenVPN status using:
    - The status log file (`/var/log/openvpn-status.log`) via cat.
3. **Throughput Calculation**: Computes throughput by comparing current and previous byte counts.
4. **Data Storage**: Inserts calculated throughput values into PostgreSQL.
5. **Cleanup**: Removes stale client entries older than 1 hour.
