# Test-only CLI (Task 7 drives it against a VHDX). Not part of the app's output.
add_executable(DiskMoveCli main.c)
set_target_properties(DiskMoveCli PROPERTIES OUTPUT_NAME diskmove-cli)
target_compile_definitions(DiskMoveCli PRIVATE UNICODE _UNICODE)
target_link_libraries(DiskMoveCli PRIVATE DiskMove)
# Run from its own folder: put DiskMove.dll next to the exe.
add_custom_command(TARGET DiskMoveCli POST_BUILD
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:DiskMove>" "$<TARGET_FILE_DIR:DiskMoveCli>")
