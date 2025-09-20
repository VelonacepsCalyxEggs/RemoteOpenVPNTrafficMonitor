# Remote OpenVPN Traffic Monitor

A background service that monitors OpenVPN client traffic statistics by connecting to an OpenVPN server via SSH, parsing traffic data, and storing throughput metrics in a PostgreSQL database.

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
2. Set your secrets:
   ```bash
   dotnet user-secrets set "vpnServerHostname" "your-vpn-server.com"
   dotnet user-secrets set "vpnServerPort" "22"
   dotnet user-secrets set "sshUsername" "your-ssh-username"
   dotnet user-secrets set "vpnServerPassword" "your-ssh-password"
   dotnet user-secrets set "connectionString" "Host=localhost;Username=postgres;Password=your-db-password"
   ```

### Configuration Parameters

- **PollingInterval**: Time in seconds between data collection cycles (minimum: 1)
- **vpnServerHostname**: OpenVPN server hostname or IP address
- **vpnServerPort**: SSH port number (default: 22)
- **sshUsername**: SSH username for server authentication
- **vpnServerPassword**: SSH password for server authentication
- **connectionString**: PostgreSQL connection string

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
- Creates a `traffic` table with the following schema:

```sql
CREATE TABLE traffic (
    client_name VARCHAR(255) NOT NULL,
    ip_addr VARCHAR(15) NOT NULL,
    bytes_in BIGINT NOT NULL CHECK (bytes_in >= 0),
    bytes_out BIGINT NOT NULL CHECK (bytes_out >= 0),
    measured_at TIMESTAMP DEFAULT NOW()
);
```

## How It Works

1. **SSH Connection**: Establishes an SSH connection to the OpenVPN server.
2. **Data Collection**: Retrieves OpenVPN status using either:
    - The management interface:
      ```bash
      echo "status 3" | nc 127.0.0.1 7505
      ```
    - The status log file (`/var/log/openvpn-status.log`) as fallback.
3. **Throughput Calculation**: Computes throughput by comparing current and previous byte counts.
4. **Data Storage**: Inserts calculated throughput values into PostgreSQL.
5. **Cleanup**: Removes stale client entries older than 1 hour.
