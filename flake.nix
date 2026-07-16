{
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    {  self, nixpkgs }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "x86_64-darwin"
        "aarch64-darwin"
      ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f (pkgsFor system));
      pkgsFor =
        system:
        import nixpkgs {
          inherit system;
          config.allowUnfree = true; # prebuilt .net is unfree
        };

      sdkFor = pkgs: pkgs.dotnetCorePackages.sdk_10_0;
    in
    {
      devShells = forAllSystems (
        pkgs:
        let
          dotnet = sdkFor pkgs;
          runtimeLibs = with pkgs; [
            libglvnd
            libGL
            libx11
            libxi
            libxrandr
            libxcursor
            libxext
            libxinerama
            libxkbcommon
            icu
            fontconfig
            freetype
            zlib
            openssl
            stdenv.cc.cc.lib
          ];
        in
        {
          default = pkgs.mkShell {
            name = "hoianviewer-dotnet10";

            # ffmpeg for export, zenity for tinyfiledialogs' file pickers
            packages = [
              dotnet
              pkgs.ffmpeg
              pkgs.zenity
            ];

            env = {
              DOTNET_ROOT = "${dotnet}";
              DOTNET_CLI_TELEMETRY_OPTOUT = "1";
              DOTNET_NOLOGO = "1";
            };

            shellHook = ''
              export NUGET_PACKAGES="$PWD/.nuget/packages"
              export LD_LIBRARY_PATH="/run/opengl-driver/lib:${pkgs.lib.makeLibraryPath runtimeLibs}''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
              launch() {
                dotnet run --project PlayerViewer -c "''${CONFIG:-Debug}" "$@"
              }
              launch_tracy() {
                DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 \
                  dotnet run --project PlayerViewer -c "''${CONFIG:-Release}" --property:Tracy=true "$@"
              }
            '';
          };
        }
      );

      formatter = forAllSystems (pkgs: pkgs.nixpkgs-fmt);
    };
}
