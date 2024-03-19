// Define an environment named "dev"
env "dev" {
  // Declare where the schema definition resides.
  // Also supported: ["file://multi.hcl", "file://schema.hcl"].
  src = "file://schema.pg.hcl"

  // Define the URL of the database which is managed
  // in this environment.
  url =  "postgres://postgres:hqysxvqw5cgdYyQHTfHe@192.168.1.20:5432/Valtuutus?sslmode=disable"
  dev = "postgres://postgres:hqysxvqw5cgdYyQHTfHe@192.168.1.20:5432/Valtuutus-dev?sslmode=disable"

  migration {
    // URL where the migration directory resides.
    dir = "file://migrations"
    // An optional format of the migration directory:
    // atlas (default) | flyway | liquibase | goose | golang-migrate | dbmate
    format = atlas
  }
}