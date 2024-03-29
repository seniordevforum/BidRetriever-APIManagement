﻿using System;
using System.Collections.Generic;
using _440DocumentManagement.Models;
using _440DocumentManagement.Helpers;
using Microsoft.AspNetCore.Mvc;
using _440DocumentManagement.Models.Document;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using _440DocumentManagement.Services.Interface;
using Amazon.Lambda;
using System.Linq;
using NSwag.Annotations;

namespace _440DocumentManagement.Controllers
{
	[Produces("application/json")]
	[Route("api")]
	[OpenApiTag("Document Management")]
	public class DocumentManagementController : Controller
	{
		private DatabaseHelper _dbHelper;
		private readonly IDocumentManagementService _documentManagementService;
		private readonly IAmazonDynamoDB _dynamoDBClient;
		private readonly IAmazonLambda _lambdaClient;

		public DocumentManagementController(
			IAmazonDynamoDB dynamoDBClient,
			IAmazonLambda lambdaClient,
			IDocumentManagementService documentManagementService)
		{
			_dbHelper = new DatabaseHelper();
			_dynamoDBClient = dynamoDBClient;
			_lambdaClient = lambdaClient;
			_documentManagementService = documentManagementService;
		}


		[HttpPost]
		[Route("CreateProjectDocument")]
		public IActionResult Post(ProjectDocument projectDocument)
		{
			// var folderContentId = Guid.NewGuid().ToString();
            // var secondFolderContentId = Guid.NewGuid().ToString();
			var docId = projectDocument.doc_id ?? Guid.NewGuid().ToString();

			try
			{
				// check missing parameter
				var missingParameter = projectDocument.CheckRequiredParameters(new string[]
				{
					"file_id",
					"project_id",
					"file_size",
					"status",
					"file_original_filename",
					"submission_id",
					"submission_datetime"
				});

				if (missingParameter != null)
				{
					return BadRequest(new
					{
						status = $"{missingParameter} is required"
					});
				}

				var projectId = projectDocument.project_id;
				var timestamp = DateTime.UtcNow;
                var customerId = __getProjectCustomerId(projectId);
                var docRevision = projectDocument.doc_revision;

                // determine document revision for same subproject/page number documents
                if (!string.IsNullOrEmpty(projectDocument.doc_number) && !string.IsNullOrEmpty(projectDocument.doc_subproject) && !string.IsNullOrEmpty(projectDocument.doc_pagenumber))
                {
                    using (var cmd = _dbHelper.SpawnCommand())
                    {
                        cmd.CommandText = "SELECT doc_id, doc_revision, doc_next_rev, submission_datetime FROM project_documents "
                            + $"WHERE project_id='{projectId}' AND doc_number='{projectDocument.doc_number}' AND "
                            + $"doc_subproject = '{projectDocument.doc_subproject}' AND doc_pagenumber = '{projectDocument.doc_pagenumber}' "
                            + "ORDER BY doc_revision";

                        using (var reader = cmd.ExecuteReader())
                        {
                            var matchedDocuments = new List<Dictionary<string, string>> { };
                            while (reader.Read())
                            {
                                matchedDocuments.Add(new Dictionary<string, string>
                                {
                                    { "doc_id", _dbHelper.SafeGetString(reader, 0) },
                                    { "doc_revision", _dbHelper.SafeGetString(reader, 1) },
                                    { "doc_next_rev", _dbHelper.SafeGetString(reader, 2) },
                                    { "submission_datetime", _dbHelper.SafeGetString(reader, 3) },
                                });
                            }

                            if (matchedDocuments.Count == 0)
                            {
                                docRevision = null;
                            }
                            else
                            {
                                var currentDoc = new Dictionary<string, object>
                                {
                                    { "submission_datetime", projectDocument.submission_datetime },
                                };
                                docRevision = _documentManagementService.GenerateDocRevision(_dbHelper, customerId, currentDoc, matchedDocuments);
                                var lastDocument = matchedDocuments.Find(document => string.IsNullOrEmpty((string)document["doc_next_rev"]));

                                reader.Close();

                                cmd.CommandText = $"UPDATE project_documents SET doc_next_rev='{docId}' WHERE doc_id='{lastDocument["doc_id"]}'";
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                }

                // create project document
                var createProjectDocumentResult = Post(new DLProjectDocument()
                {
                    doc_id = docId,
                    doc_name = projectDocument.doc_name,
                    doc_name_abbrv = projectDocument.doc_name_abbrv,
                    doc_number = projectDocument.doc_number,
                    doc_revision = docRevision,
                    doc_parent_id = projectDocument.doc_parent_id,
                    doc_type = projectDocument.doc_type,
                    status = projectDocument.status,
                    project_id = projectId,
                    submission_id = projectDocument.submission_id,
                    submission_datetime = projectDocument.submission_datetime,
                    project_doc_original_filename = projectDocument.file_original_filename,
                    process_status = projectDocument.process_status,
                    display_name = projectDocument.display_name,
                    doc_size = projectDocument.doc_size,
                    doc_subproject = projectDocument.doc_subproject,
                    doc_sequence = projectDocument.doc_sequence,
                    doc_pagenumber = projectDocument.doc_pagenumber,
				}, true);

				if (createProjectDocumentResult is BadRequestObjectResult)
				{
					return createProjectDocumentResult;
				}

				var isDuplicatedDocument = ((OkObjectResult)createProjectDocumentResult).Value.ToString().Contains("duplicated");

				if (!isDuplicatedDocument)
				{
                    // Note: Do not create folder content records, these will be handled by 922
                    /*
					// create source_current folder structure, except split files
                    // Note: commented this line to resolve folder content issue for original plan files
					//if (projectDocument.process_status == "completed" && string.IsNullOrEmpty(projectDocument.doc_parent_id))
                    if (string.IsNullOrEmpty(projectDocument.doc_parent_id))
                        {
                            var isSourceFileSubmissionEnabled = __isSourceFileSubmissionFolderEnabled(projectDocument.project_id);

						var createFolderContentResult = Post(new DLFolderContent()
						{
							project_id = projectDocument.project_id,
							folder_type = "source_current",
							folder_content_id = folderContentId,
							folder_path = projectDocument.folder_path,
							doc_id = docId,
							file_id = projectDocument.file_id,
							status = "active",
							folder_original_filename = projectDocument.file_original_filename,
						}, true);

						if (createFolderContentResult is BadRequestObjectResult)
						{
							__deleteProjectDocument(docId);
							return createFolderContentResult;
						}

						if (isSourceFileSubmissionEnabled)
						{
							var submissionDateTime = DateTimeHelper.GetFormattedTimestamp(
								projectDocument.submission_datetime,
								"yyyy-MM-dd_HH-mm");
							var folderPath = string.IsNullOrEmpty(projectDocument.folder_path)
								? submissionDateTime
								: $"{submissionDateTime}/{projectDocument.folder_path}";

							createFolderContentResult = Post(new DLFolderContent()
							{
								project_id = projectDocument.project_id,
								folder_type = "source_history",
								folder_content_id = secondFolderContentId,
								folder_path = folderPath,
								doc_id = docId,
								file_id = projectDocument.file_id,
								status = "active",
								folder_original_filename = projectDocument.file_original_filename,
							}, true);

							if (createFolderContentResult is BadRequestObjectResult)
							{
								__deleteProjectDocument(docId);
								return createFolderContentResult;
							}
						}
					}
                    */

					// copy files if file_id is duplicate. otherwise, add file to the document
					if ((projectDocument.doc_type == "original_single_sheet_plan" || projectDocument.doc_type == "split_single_sheet_plan")
						&& __checkFileIdExists(projectDocument.file_id))
					{
						// copy all “files” related to the project_document already linked to the file_id
						var copyResult = __copyFiles(projectDocument.file_id, docId);

						if (copyResult > 0)
						{
                            return Ok(new
                            {
                                doc_id = docId,
                                status = "completed"
                            });
                        }
					}

					// create file / document file 
					var addDocumentFileResult = Post(new ProjectDocumentFile()
					{
						doc_id = docId,
						file_id = projectDocument.file_id,
						file_size = projectDocument.file_size,
						file_type = "source_system_original",
						file_original_filename = projectDocument.file_original_filename,
						bucket_name = projectDocument.bucket_name ?? "",
						file_original_application = projectDocument.file_original_application,
						file_original_author = projectDocument.file_original_author,
						file_original_pdf_version = projectDocument.file_original_pdf_version,
						file_original_document_title = projectDocument.file_original_document_title,
						file_original_create_datetime = projectDocument.file_original_create_datetime,
						file_original_modified_datetime = projectDocument.file_original_modified_datetime,
						parent_original_create_datetime = projectDocument.parent_original_create_datetime,
						parent_original_modified_datetime = projectDocument.parent_original_modified_datetime,
						file_original_document_bookmark = projectDocument.file_original_document_bookmark,
					}, true);

					if (addDocumentFileResult is BadRequestObjectResult)
					{
						__deleteProjectDocument(docId);
						// __deleteFolderContent(folderContentId);
						return addDocumentFileResult;
					}
                }

                return Ok(new
                {
                    doc_id = docId,
                    status = "completed"
                });
            }
			catch (Exception exception)
			{
				// __deleteFolderContent(folderContentId);
				__deleteProjectDocument(docId);
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("UpdateProjectDocument")]
		public IActionResult Post(ProjectDocumentUpdateRequest request, bool isInternalRequest = false)
		{
			try
			{
				// validation check
				if (request.search_project_document_id == null)
				{
					return BadRequest(new
					{
						status = "search_project_document_id is required"
					});
				}

				// update
				using (var cmd = _dbHelper.SpawnCommand())
				{
					var whereString = $" WHERE doc_id='{request.search_project_document_id}'";
					var queryString = @"UPDATE project_documents SET "
						+ "doc_name = COALESCE(@doc_name, doc_name), "
						+ "doc_name_abbrv = COALESCE(@doc_name_abbrv, doc_name_abbrv), "
						+ "doc_number = COALESCE(@doc_number, doc_number), "
						+ "doc_version = COALESCE(@doc_version, doc_version), "
						+ "doc_discipline = COALESCE(@doc_discipline, doc_discipline), "
						+ "doc_desc = COALESCE(@doc_desc, doc_desc), "
						+ "status = COALESCE(@status, status), "
						+ "doc_type = COALESCE(@doc_type, doc_type), "
						+ "process_status = COALESCE(@process_status, process_status), "
						+ "display_name = COALESCE(@display_name, display_name), "
						+ "doc_size = COALESCE(@doc_size, doc_size), "
						+ "edit_datetime = @edit_datetime, "
						+ "doc_pagenumber = COALESCE(@doc_pagenumber, doc_pagenumber), "
						+ "doc_sequence = COALESCE(@doc_sequence, doc_sequence), "
						+ "doc_subproject = COALESCE(@doc_subproject, doc_subproject)";

					if (true == true) // check if api_key has admin access
					{
						queryString += ", "
                            + (request.doc_revision != "NULL" ? "doc_revision = COALESCE(@doc_revision, doc_revision), " : "doc_revision = NULL, ")
							+ (request.doc_next_rev != "NULL" ? "doc_next_rev = COALESCE(@doc_next_rev, doc_next_rev)" : "doc_next_rev = NULL");
					}

					queryString = queryString + whereString;

					cmd.CommandText = queryString;
					cmd.Parameters.AddWithValue("doc_name", (object)request.doc_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_name_abbrv", (object)request.doc_name_abbrv ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_number", (object)request.doc_number ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_version", (object)request.doc_version ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_discipline", (object)request.doc_discipline ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_desc", (object)request.doc_desc ?? DBNull.Value);
					cmd.Parameters.AddWithValue("status", (object)request.status ?? DBNull.Value);
					cmd.Parameters.AddWithValue("edit_datetime", DateTime.UtcNow);
					cmd.Parameters.AddWithValue("doc_revision", (object)request.doc_revision ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_next_rev", (object)request.doc_next_rev ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_type", (object)request.doc_type ?? DBNull.Value);
					cmd.Parameters.AddWithValue("display_name", (object)request.display_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_size", (object)request.doc_size ?? DBNull.Value);
					cmd.Parameters.AddWithValue("process_status", (object)request.process_status ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_pagenumber", (object)request.doc_pagenumber ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_sequence", (object)request.doc_sequence ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_subproject", (object)request.doc_subproject ?? DBNull.Value);

					if (cmd.ExecuteNonQuery() == 0)
					{
						return BadRequest(new
						{
							status = "no matching project document found"
						});
					}

                    // We need to remove existing doc_name if sheet is indexed but no name is entered
					// Well, this is not a good design. Should be fixed
                    if (!isInternalRequest && !string.IsNullOrEmpty(request.doc_number) && string.IsNullOrEmpty(request.doc_name))
                    {
                        cmd.CommandText = $"UPDATE project_documents SET doc_name=NULL WHERE doc_id='{request.search_project_document_id}'";
                        cmd.ExecuteNonQuery();
                    }
				}

				return Ok(new
				{
					status = "completed"
				});
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (!isInternalRequest)
				{
					_dbHelper.CloseConnection();
				}
			}
		}

		[HttpPost]
		[Route("UpdateFile")]
		public IActionResult Post(FileUpdateRequest request)
		{
			try
			{
				// validation check
				if (request.search_file_id == null)
				{
					return BadRequest(new { status = "search_file_id is required" });
				}

				// update
				using (var cmd = _dbHelper.SpawnCommand())
				{
					var whereString = $" WHERE file_id='{request.search_file_id}'";
					var queryString = "UPDATE files SET "
													+ "file_original_application = COALESCE(@file_original_application, file_original_application), "
													+ "file_original_author = COALESCE(@file_original_author, file_original_author), "
													+ "file_original_create_datetime = COALESCE(@file_original_create_datetime, file_original_create_datetime), "
													+ "file_original_modified_datetime = COALESCE(@file_original_modified_datetime, file_original_modified_datetime), "
													+ "file_original_document_title = COALESCE(@file_original_document_title, file_original_document_title), "
													+ "file_original_pdf_version = COALESCE(@file_original_pdf_version, file_original_pdf_version), "
													+ "parent_original_create_datetime = COALESCE(@parent_original_create_datetime, parent_original_create_datetime), "
													+ "parent_original_modified_datetime = COALESCE(@parent_original_modified_datetime, parent_original_modified_datetime), "
													+ "file_original_document_bookmark = COALESCE(@file_original_document_bookmark, file_original_document_bookmark), "
													+ "edit_datetime = @edit_datetime";

					queryString = queryString + whereString;

					cmd.CommandText = queryString;
					cmd.Parameters.AddWithValue(
						"file_original_application",
						(object)request.file_original_application ?? DBNull.Value);
					cmd.Parameters.AddWithValue(
						"file_original_author",
						(object)request.file_original_author ?? DBNull.Value);
					cmd.Parameters.AddWithValue(
						"file_original_create_datetime",
						request.file_original_create_datetime != null ? (object)DateTimeHelper.ConvertToUTCDateTime(request.file_original_create_datetime) : DBNull.Value);
					cmd.Parameters.AddWithValue(
						"file_original_modified_datetime",
						request.file_original_modified_datetime != null ? (object)DateTimeHelper.ConvertToUTCDateTime(request.file_original_modified_datetime) : DBNull.Value);
					cmd.Parameters.AddWithValue(
						"file_original_document_title",
						(object)request.file_original_document_title ?? DBNull.Value);
					cmd.Parameters.AddWithValue(
						"file_original_pdf_version",
						(object)request.file_original_pdf_version ?? DBNull.Value);
					cmd.Parameters.AddWithValue(
						"parent_original_create_datetime",
						request.parent_original_create_datetime != null ? (object)DateTimeHelper.ConvertToUTCDateTime(request.parent_original_create_datetime) : DBNull.Value);
					cmd.Parameters.AddWithValue(
						"parent_original_modified_datetime",
						request.parent_original_modified_datetime != null ? (object)DateTimeHelper.ConvertToUTCDateTime(request.parent_original_modified_datetime) : DBNull.Value);
					cmd.Parameters.AddWithValue(
						"file_original_document_bookmark",
						(object)request.file_original_document_bookmark ?? DBNull.Value);
					cmd.Parameters.AddWithValue("edit_datetime", DateTime.UtcNow);

					if (cmd.ExecuteNonQuery() == 0)
					{
						return BadRequest(new
						{
							status = "no matching file found"
						});
					}
					else
					{
						return Ok(new
						{
							status = "completed"
						});
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("AddProjectDocumentFile")]
		public IActionResult Post(ProjectDocumentFile projectDocumentFile, bool isInternalRequest = false)
		{
			var docFileId = Guid.NewGuid().ToString();

			try
			{
				// check missing parameter
				var missingParameter = projectDocumentFile.CheckRequiredParameters(new string[]
				{ "doc_id", "file_id", "file_type", "file_size", "file_original_filename" });

				if (missingParameter != null)
				{
					return BadRequest(new
					{
						status = $"{missingParameter} is required"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{

					// check if doc_id exists
					cmd.CommandText = $"SELECT EXISTS (SELECT true FROM project_documents WHERE doc_id='{projectDocumentFile.doc_id}')";

					if ((bool)cmd.ExecuteScalar() == false)
					{
						return BadRequest(new
						{
							status = "doc_id doesn't exist"
						});
					}

					// check if they are already linked
					cmd.CommandText = $"SELECT EXISTS (SELECT true FROM document_files WHERE doc_id='{projectDocumentFile.doc_id}' AND file_id='{projectDocumentFile.file_id}')";

					if ((bool)cmd.ExecuteScalar() == true)
					{
						return Ok(new
						{
							status = "specified doc and file were already linked"
						});
					}

					// create files record
					var fileExtension = StringHelper.GetFileExtension(projectDocumentFile.file_original_filename);
					var fileKey = projectDocumentFile.file_id.Substring(0, 2) + '/' + projectDocumentFile.file_id.Substring(2, 1) + '/' + projectDocumentFile.file_id + '/' + projectDocumentFile.file_id + (fileExtension != null ? ("." + fileExtension.ToLower()) : "");
					var createFileResult = Post(new DLFile()
					{
						file_id = projectDocumentFile.file_id,
						file_type = projectDocumentFile.file_type,
						file_size = projectDocumentFile.file_size,
						file_key = fileKey,
						file_original_filename = projectDocumentFile.file_original_filename,
						bucket_name = projectDocumentFile.bucket_name ?? "",
						status = projectDocumentFile.status ?? "active",
						file_original_application = projectDocumentFile.file_original_application,
						file_original_author = projectDocumentFile.file_original_author,
						file_original_pdf_version = projectDocumentFile.file_original_pdf_version,
						file_original_document_title = projectDocumentFile.file_original_document_title,
						file_original_create_datetime = projectDocumentFile.file_original_create_datetime,
						file_original_modified_datetime = projectDocumentFile.file_original_modified_datetime,
						parent_original_create_datetime = projectDocumentFile.parent_original_create_datetime,
						parent_original_modified_datetime = projectDocumentFile.parent_original_modified_datetime,
						file_original_document_bookmark = projectDocumentFile.file_original_document_bookmark,
					}, true);

					if (createFileResult is BadRequestObjectResult)
					{
						return createFileResult;
					}

					// create document_files record
					var createDocumentFileResult = Post(new DLDocumentFile()
					{
						doc_id = projectDocumentFile.doc_id,
						file_id = projectDocumentFile.file_id,
						doc_file_id = docFileId,
					}, true);

					if (createDocumentFileResult is BadRequestObjectResult)
					{
						__deleteFile(projectDocumentFile.file_id);
						return createDocumentFileResult;
					}

					return Ok(new
					{
						doc_file_id = docFileId,
						status = ((OkObjectResult)createFileResult).Value.ToString().Contains("duplicated") ? "duplicated" : "completed"
					});
				}
			}
			catch (Exception exception)
			{
				__deleteFile(projectDocumentFile.file_id);
				__deleteDocumentFile(docFileId);

				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (isInternalRequest == false)
				{
					_dbHelper.CloseConnection();
				}
			}
		}


		[HttpGet]
		[Route("GetDocumentsByFileHash")]
		public IActionResult Get([FromQuery(Name = "doc_hash")] string doc_hash)
		{
			try
			{
				if (string.IsNullOrEmpty(doc_hash))
				{
					return BadRequest(new
					{
						status = "doc_hash is required"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "SELECT project_documents.doc_id, project_documents.doc_type, project_documents.project_id, "
							+ "projects.project_admin_user_id, users.customer_id, project_documents.project_doc_original_filename, files.file_type, "
							+ "files.file_id, files.file_size, project_documents.status, project_folder_contents.folder_path, "
							+ "project_documents.process_status, project_submissions.submission_name, project_documents.create_datetime, "
							+ "project_folder_contents.folder_id "
							+ "FROM files "
							+ "LEFT JOIN document_files ON document_files.file_id=files.file_id "
							+ "LEFT JOIN project_documents ON project_documents.doc_id=document_files.doc_id "
							+ "LEFT JOIN projects ON project_documents.project_id=projects.project_id "
							+ "LEFT JOIN users ON projects.project_admin_user_id=users.user_id "
							+ "LEFT JOIN project_folder_contents ON project_folder_contents.doc_id=project_documents.doc_id "
							+ "LEFT JOIN project_submissions ON project_submissions.project_submission_id=project_documents.submission_id "
							+ "WHERE files.file_id='" + doc_hash + "'";

					using (var reader = cmd.ExecuteReader())
					{
						var resultArray = new List<Dictionary<string, string>>();

						while (reader.Read())
						{
							var record = new Dictionary<string, string>()
							{
								{ "doc_id", _dbHelper.SafeGetString(reader, 0) },
								{ "doc_type", _dbHelper.SafeGetString(reader, 1) },
								{ "project_id", _dbHelper.SafeGetString(reader, 2) },
								{ "project_admin_user_id", _dbHelper.SafeGetString(reader, 3) },
								{ "customer_id", _dbHelper.SafeGetString(reader, 4) },
								{ "file_original_filename", _dbHelper.SafeGetString(reader, 5) },
								{ "file_type", _dbHelper.SafeGetString(reader, 6) },
								{ "file_id", _dbHelper.SafeGetString(reader, 7) },
								{ "file_size", _dbHelper.SafeGetString(reader, 8) },
								{ "status", _dbHelper.SafeGetString(reader, 9) },
								{ "process_status", _dbHelper.SafeGetString(reader, 11) },
								{ "create_datetime", _dbHelper.SafeGetDatetimeString(reader, 13) },
								{ "folder_id", _dbHelper.SafeGetString(reader, 14) }
							};

							var folderPath = _dbHelper.SafeGetString(reader, 10);
							var submissionName = _dbHelper.SafeGetString(reader, 12);

                            /*
							if (!string.IsNullOrEmpty(submissionName) && folderPath.StartsWith(submissionName))
							{
								folderPath = folderPath.Replace(submissionName, "").TrimStart('/');
							}
							else
							{
								folderPath = Regex.Replace(folderPath, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}", "").TrimStart('/');
							}
                            */

							record["folder_path"] = folderPath;

							resultArray.Add(record);
						}

						reader.Close();
						return Ok(resultArray);
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					error = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("CreateProjectFolderDL")]
		public IActionResult Post(DLProjectFolder projectFolder, bool isInternalRequest = false)
		{
			try
			{
				// check missing parameter
				var missingParameter = projectFolder.CheckRequiredParameters(new string[]
				{
					"project_id", "folder_name", "folder_type"
				});

				if (missingParameter != null)
				{
					return BadRequest(new
					{
						status = $"{missingParameter} is required"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					var folderId = projectFolder.folder_id ?? Guid.NewGuid().ToString();
					var parentFolderId = projectFolder.parent_folder_id;
					var timestamp = DateTime.UtcNow;

					// check folder_id duplication
					if (__checkFolderExists(folderId) == true)
					{
						return Ok(new
						{
							folder_id = folderId,
							status = "duplicated"
						});
					}

					// check parent folder existence
					if (!string.IsNullOrEmpty(parentFolderId) && !__checkFolderExists(parentFolderId))
					{
						return BadRequest(new
						{
							status = "parent_folder_id doesn't exist"
						});
					}

					// check if same folder exists inside the parent folder
					var checkResult = __checkFolderNameExists(projectFolder.project_id, parentFolderId, projectFolder.folder_name);

					if (checkResult != null)
					{
						return Ok(new
						{
							folder_id = checkResult,
							status = "same folder already exists"
						});
					}

					// now, create record
					folderId = __createFolder(
						projectFolder.project_id,
						folderId,
						projectFolder.folder_name,
						parentFolderId,
						projectFolder.folder_type,
						projectFolder.status ?? "active",
						timestamp);

					return Ok(new
					{
						folder_id = folderId,
						status = "completed"
					});
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (isInternalRequest == false)
				{
					_dbHelper.CloseConnection();
				}
			}
		}


		[HttpPost]
		[Route("CreateProjectDocumentDL")]
		public IActionResult Post(DLProjectDocument projectDocument, bool isInternalRequest = false)
		{
			try
			{
				var missingParameter = projectDocument.CheckRequiredParameters(new string[]
				{ "project_id", "submission_id", "submission_datetime" });

				if (missingParameter != null)
				{
					return BadRequest(new
					{
						status = $"{missingParameter} is required"
					});
				}

				var timestamp = DateTime.UtcNow;
				var docId = projectDocument.doc_id ?? Guid.NewGuid().ToString();

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = $"SELECT EXISTS (SELECT true FROM project_documents WHERE doc_id='{docId}')";

					if ((bool)cmd.ExecuteScalar() == false)
					{
						cmd.CommandText = "INSERT INTO project_documents (doc_id, project_id, submission_id, submission_datetime, doc_number, doc_revision, "
												+ "doc_name, doc_type, doc_parent_id, status, create_datetime, "
												+ "edit_datetime, project_doc_original_filename, process_status, doc_name_abbrv, display_name, doc_size, doc_subproject, doc_pagenumber, doc_sequence) "
												+ "VALUES(@doc_id, @project_id, @submission_id, @submission_datetime, @doc_number, @doc_revision, @doc_name, @doc_type, @doc_parent_id, "
												+ "@status, @create_datetime, @edit_datetime, @project_doc_original_filename, @process_status, @doc_name_abbrv, @display_name, @doc_size, @doc_subproject, @doc_pagenumber, @doc_sequence) ON CONFLICT DO NOTHING";

						cmd.Parameters.AddWithValue("doc_id", docId);
						cmd.Parameters.AddWithValue("project_id", projectDocument.project_id);
						cmd.Parameters.AddWithValue("submission_id", projectDocument.submission_id);
						cmd.Parameters.AddWithValue("submission_datetime", DateTimeHelper.ConvertToUTCDateTime(projectDocument.submission_datetime));
						cmd.Parameters.AddWithValue("doc_number", projectDocument.doc_number ?? "");
						cmd.Parameters.AddWithValue("doc_revision", projectDocument.doc_revision ?? "");
						cmd.Parameters.AddWithValue("doc_name", projectDocument.doc_name ?? "");
						cmd.Parameters.AddWithValue("doc_name_abbrv", projectDocument.doc_name_abbrv ?? "");
						cmd.Parameters.AddWithValue("doc_type", projectDocument.doc_type ?? "");
						cmd.Parameters.AddWithValue("doc_parent_id", projectDocument.doc_parent_id ?? "");
						cmd.Parameters.AddWithValue("status", projectDocument.status ?? "active");
						cmd.Parameters.AddWithValue("create_datetime", timestamp);
						cmd.Parameters.AddWithValue("edit_datetime", timestamp);
						cmd.Parameters.AddWithValue("project_doc_original_filename", projectDocument.project_doc_original_filename ?? "");
						cmd.Parameters.AddWithValue("process_status", projectDocument.process_status ?? "queued");
						cmd.Parameters.AddWithValue("display_name", projectDocument.display_name ?? "");
						cmd.Parameters.AddWithValue("doc_size", projectDocument.doc_size ?? "");
                        cmd.Parameters.AddWithValue("doc_subproject", projectDocument.doc_subproject ?? "");
                        cmd.Parameters.AddWithValue("doc_pagenumber", projectDocument.doc_pagenumber ?? "");
                        cmd.Parameters.AddWithValue("doc_sequence", (object)projectDocument.doc_sequence ?? DBNull.Value);

						int rowsAffected = cmd.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            return Ok(new
                            {
                                doc_id = docId,
                                status = "completed"
                            });
                        }
                        else
                        {
                            return BadRequest(new
                            {
                                status = "Failed to create project document"
                            });
                        }
					}
					else
					{
						return Ok(new
						{
							doc_id = docId,
							status = "duplicated"
						});
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (isInternalRequest == false)
				{
					_dbHelper.CloseConnection();
				}
			}
		}


		[HttpPost]
		[Route("CreateFolderContentDL")]
		public IActionResult Post(DLFolderContent folderContent, bool isInternalRequest = false)
		{
			try
			{
				if (string.IsNullOrEmpty(folderContent.project_id))
				{
					return BadRequest(new { status = "Please provide project_id" });
				}
				if (string.IsNullOrEmpty(folderContent.doc_id) || string.IsNullOrEmpty(folderContent.file_id))
				{
					return BadRequest(new { status = "Please provide doc_id and file_id" });
				}
				if (string.IsNullOrEmpty(folderContent.folder_id) && string.IsNullOrEmpty(folderContent.folder_type))
				{
					return BadRequest(new { status = "Please provide folder_type or folder_id" });
				}

				var timestamp = DateTime.UtcNow;
				var folderContentId = folderContent.folder_content_id ?? Guid.NewGuid().ToString();
				var folderId = folderContent.folder_id ?? "";
				var submissionName = __getSubmissionName(folderContent.submission_id);

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = $"SELECT EXISTS (SELECT true FROM project_folder_contents WHERE folder_content_id='{folderContentId}')";

					if ((bool)cmd.ExecuteScalar() == false)
					{
						cmd.CommandText = $"SELECT EXISTS (SELECT true FROM project_documents WHERE doc_id='{folderContent.doc_id}')";

						if ((bool)cmd.ExecuteScalar() == false)
						{
							return BadRequest(new { status = "doc_id doesn't exist" });
						}

						if (string.IsNullOrEmpty(folderId))
						{
							folderId = __getRootFolderId(folderContent.project_id, folderContent.folder_type);

							if (folderId == null)
							{
								folderId = Guid.NewGuid().ToString();

								folderId = __createFolder(
									folderContent.project_id,
									folderId,
									__getRootFolderName(folderContent.folder_type),
									null,
									folderContent.folder_type,
									"active",
									timestamp);
							}

							if (!string.IsNullOrEmpty(folderContent.folder_path))
							{
								var folderNames = folderContent.folder_path.Split("/");

								if (folderNames.Length == 0)
								{
									return BadRequest(new { status = "invalid folder_path" });
								}

								var matchedStartIndex = 0;
								while (matchedStartIndex < folderNames.Length)
								{
									if (folderNames[matchedStartIndex] != __getRootFolderName(folderContent.folder_type))
									{
										break;
									}

									matchedStartIndex++;
								}
								folderNames = folderNames.Where((item, index) => index >= matchedStartIndex).ToArray();

								for (var index = 0; index < folderNames.Length; index++)
								{
									var checkResult = __checkFolderNameExists(folderContent.project_id, folderId, folderNames[index]);

									if (checkResult == null)
									{
										var id = Guid.NewGuid().ToString();

										if (!string.IsNullOrEmpty(folderContent.submission_id) && folderNames[index] == submissionName)
										{
                                            id = __createFolder(
												folderContent.project_id,
												id,
												folderNames[index],
												folderId,
												folderContent.folder_type,
												"active",
												timestamp,
												folderContent.submission_id);
										}
										else
										{
                                            id = __createFolder(
												folderContent.project_id,
												id,
												folderNames[index],
												folderId,
												folderContent.folder_type,
												"active",
												timestamp);
										}

										folderId = id;
									}
									else
									{
										folderId = checkResult;
									}
								}
							}
						}
						else if (!__checkFolderExists(folderId))
						{
							return BadRequest(new
							{
								status = "Provided folder_id doesn't exist"
							});
						}

						if (__checkFolderContentExists(folderId, folderContent.doc_id))
						{
							// Update
							cmd.CommandText = "UPDATE project_folder_contents SET "
															+ "folder_content_id = @folder_content_id, "
															+ "folder_original_filename = COALESCE(@folder_original_filename, folder_original_filename), "
															+ "status = COALESCE(@status, status), "
															+ "edit_datetime = @edit_datetime "
															+ "WHERE status='active' AND folder_id='" + folderId + "' AND doc_id='" + folderContent.doc_id + "'";

							cmd.Parameters.AddWithValue("folder_content_id", folderContentId);
							cmd.Parameters.AddWithValue("folder_original_filename", folderContent.folder_original_filename ?? "");
							cmd.Parameters.AddWithValue("status", folderContent.status ?? "active");
							cmd.Parameters.AddWithValue("edit_datetime", DateTime.UtcNow);

							cmd.ExecuteNonQuery();
						}
						else
						{
							// insert
							cmd.CommandText = "INSERT INTO project_folder_contents (folder_content_id, folder_id, folder_path, folder_original_filename, "
											+ "doc_id, file_id, status, create_datetime, edit_datetime) "
											+ "VALUES(@folder_content_id, @folder_id, @folder_path, @folder_original_filename, @doc_id, @file_id, @status, "
											+ "@create_datetime, @edit_datetime) ON CONFLICT DO NOTHING";

							cmd.Parameters.AddWithValue("folder_content_id", folderContentId);
							cmd.Parameters.AddWithValue("folder_id", folderId);
							cmd.Parameters.AddWithValue("folder_path", folderContent.folder_path ?? "");
							cmd.Parameters.AddWithValue("folder_original_filename", folderContent.folder_original_filename ?? "");
							cmd.Parameters.AddWithValue("doc_id", folderContent.doc_id);
							cmd.Parameters.AddWithValue("file_id", folderContent.file_id);
							cmd.Parameters.AddWithValue("status", folderContent.status ?? "active");
							cmd.Parameters.AddWithValue("create_datetime", timestamp);
							cmd.Parameters.AddWithValue("edit_datetime", timestamp);

							cmd.ExecuteNonQuery();

							// update folder's content quantity
							cmd.CommandText = "UPDATE project_folders SET folder_content_quantity = folder_content_quantity + 1 "
															+ "WHERE folder_id='" + folderId + "';";
							cmd.ExecuteNonQuery();
						}
					}
					else
					{
						return Ok(new
						{
							folder_content_id = folderContentId,
							status = "duplicated"
						});
					}
				}

				// Create folder transaction log
				var folderName = __getFolderNameFromFolderId(folderId);
				_documentManagementService.CreateFolderTransactionLog(
					_dbHelper,
					new Dictionary<string, object>
					{
						{ "project_id", folderContent.project_id },
						{ "folder_id", folderId },
						{ "doc_id", folderContent.doc_id },
						{ "file_id", folderContent.file_id },
						{ "folder_path", folderContent.folder_path ?? "" },
						{ "original_folder_name", folderName },
						{ "new_folder_name", folderName },
					},
					"add_file");

				return Ok(new
				{
					folder_content_id = folderContentId,
					status = "completed"
				});
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (isInternalRequest == false)
				{
					_dbHelper.CloseConnection();
				}
			}
		}


		[HttpGet]
		[Route("GetFolderChildrenDL")]
		public IActionResult Get(DLFolderChildrenGetRequest request)
		{
			try
			{
				var detailLevel = request.detail_level ?? "basic";
				using (var cmd = _dbHelper.SpawnCommand())
				{
					var result = new List<Dictionary<string, object>> { };

					// Retrieve child folders
					var where = " WHERE status = 'active' AND ";
					where += !string.IsNullOrEmpty(request.project_id) ? $"project_id='{request.project_id}' AND " : "";
					if (!string.IsNullOrEmpty(request.folder_id))
					{
						where += $"parent_folder_id='{request.folder_id}' AND ";
					}
					else if (string.IsNullOrEmpty(request.submission_id))
					{
						where += "COALESCE(parent_folder_id, '') = '' AND ";
					}
					where += !string.IsNullOrEmpty(request.folder_type) ? $"folder_type='{request.folder_type}' AND " : "";
					where += !string.IsNullOrEmpty(request.submission_id) ? $"submission_id='{request.submission_id}' AND " : "";

					where = where.Remove(where.Length - 5);

					cmd.CommandText = "SELECT folder_id, folder_name, folder_type, folder_content_quantity "
													+ "FROM project_folders" + where + " ORDER BY create_datetime;";

					using (var reader = cmd.ExecuteReader())
					{
						while (reader.Read())
						{
							result.Add(new Dictionary<string, object>
							{
								{ "folder_id", _dbHelper.SafeGetString(reader, 0) },
								{ "folder_name", _dbHelper.SafeGetString(reader, 1) },
								{ "folder_type", _dbHelper.SafeGetString(reader, 2) },
								{ "folder_content_quantity", _dbHelper.SafeGetIntegerRaw(reader, 3) },
								{ "child_type", "folder" },
							});
						}
					}

					// Retrieve all child documents
					if (!string.IsNullOrEmpty(request.folder_id))
					{
						if (detailLevel == "compact")
						{
							cmd.CommandText = "SELECT files.bucket_name, project_documents.display_name, "
								+ "project_folder_contents.doc_id, project_documents.doc_next_rev, "
								+ "project_documents.doc_type, project_folder_contents.folder_content_id, "
								+ "project_folder_contents.file_id, files.file_size, project_documents.process_status "
								+ "FROM project_folder_contents "
								+ "LEFT JOIN project_documents ON project_documents.doc_id=project_folder_contents.doc_id "
								+ "LEFT JOIN files ON files.file_id = project_folder_contents.file_id "
								+ "LEFT JOIN project_submissions ON project_submissions.project_submission_id=project_documents.submission_id "
								+ "WHERE folder_id='" + request.folder_id + "' AND project_folder_contents.status='active' ORDER BY project_folder_contents.create_datetime";
						}
						else
						{
							cmd.CommandText = "SELECT project_folder_contents.folder_content_id, project_folder_contents.doc_id, project_folder_contents.folder_original_filename, project_folder_contents.file_id, "
														+ "project_documents.doc_name, project_documents.doc_number, project_documents.doc_revision, project_documents.create_datetime, project_documents.submission_datetime, "
														+ "project_documents.doc_type, project_documents.process_status, files.file_size, project_documents.submission_id, "
														+ "project_submissions.submission_name, project_submissions.submitter_email, project_documents.display_name, files.bucket_name, "
														+ "project_documents.doc_pagenumber, project_documents.doc_subproject "
														+ "FROM project_folder_contents "
														+ "LEFT JOIN project_documents ON project_documents.doc_id=project_folder_contents.doc_id "
														+ "LEFT JOIN files ON files.file_id = project_folder_contents.file_id "
														+ "LEFT JOIN project_submissions ON project_submissions.project_submission_id=project_documents.submission_id "
														+ "WHERE folder_id='" + request.folder_id + "' AND project_folder_contents.status='active' ORDER BY project_folder_contents.create_datetime";
						}

						using (var reader = cmd.ExecuteReader())
						{
							while (reader.Read())
							{
								if (detailLevel == "compact")
								{
									result.Add(new Dictionary<string, object>
									{
										{ "bucket_name", _dbHelper.SafeGetString(reader, 0) },
										{ "child_type", "file" },
										{ "display_name", _dbHelper.SafeGetString(reader, 1) },
										{ "doc_id", _dbHelper.SafeGetString(reader, 2) },
										{ "doc_latest_revision", string.IsNullOrEmpty(_dbHelper.SafeGetString(reader, 3)) },
										{ "doc_type", _dbHelper.SafeGetString(reader, 4) },
										{ "folder_content_id", _dbHelper.SafeGetString(reader, 5) },
										{ "file_id", _dbHelper.SafeGetString(reader, 6) },
										{ "file_size", _dbHelper.SafeGetString(reader, 7) },
										{ "process_status", _dbHelper.SafeGetString(reader, 8) }
									});
								}
								else
								{
									result.Add(new Dictionary<string, object>
									{
										{ "folder_content_id", _dbHelper.SafeGetString(reader, 0) },
										{ "doc_id", _dbHelper.SafeGetString(reader, 1) },
										{ "folder_original_filename", _dbHelper.SafeGetString(reader, 2) },
										{ "file_id", _dbHelper.SafeGetString(reader, 3) },
										{ "doc_name", _dbHelper.SafeGetString(reader, 4) },
										{ "doc_number", _dbHelper.SafeGetString(reader, 5) },
										{ "doc_revision", _dbHelper.SafeGetString(reader, 6) },
										{ "create_datetime", _dbHelper.SafeGetDatetimeString(reader, 7) },
										{ "submission_datetime", _dbHelper.SafeGetDatetimeString(reader, 8) },
										{ "doc_type", _dbHelper.SafeGetString(reader, 9) },
										{ "process_status", _dbHelper.SafeGetString(reader, 10) },
										{ "file_size", _dbHelper.SafeGetString(reader, 11) },
										{ "submission_id", _dbHelper.SafeGetString(reader, 12) },
										{ "submission_name", _dbHelper.SafeGetString(reader, 13) },
										{ "submitter_email", _dbHelper.SafeGetString(reader, 14) },
										{ "display_name", _dbHelper.SafeGetString(reader, 15) },
										{ "bucket_name", _dbHelper.SafeGetString(reader, 16) },
										{ "doc_pagenumber", _dbHelper.SafeGetString(reader, 17) },
										{ "doc_subproject", _dbHelper.SafeGetString(reader, 18) },
										{ "folder_id", request.folder_id },
										{ "child_type", "file" },
									});
								}
							}
						}
					}
					return Ok(result);
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("CreateFileDL")]
		public IActionResult Post(DLFile file, bool isInternalRequest = false)
		{
			try
			{
				var missingParameter = file.CheckRequiredParameters(new string[] { "file_id", "file_type", "file_size", "file_key" });

				if (missingParameter == null)
				{
					var timestamp = DateTime.UtcNow;
					var fileId = file.file_id;

					using (var cmd = _dbHelper.SpawnCommand())
					{

						cmd.CommandText = "SELECT EXISTS (SELECT true FROM files WHERE file_id='" + fileId + "')";

						if ((bool)cmd.ExecuteScalar() == false)
						{
							cmd.CommandText = "INSERT INTO files (file_id, file_type, file_original_filename, "
													+ "file_size, bucket_name, file_key, standard_doc_number, "
													+ "file_original_application, file_original_author, file_original_document_title, "
													+ "file_original_create_datetime, file_original_modified_datetime, file_original_pdf_version, "
													+ "parent_original_create_datetime, parent_original_modified_datetime, file_original_document_bookmark, "
													+ "status, create_datetime, edit_datetime) "
													+ "VALUES(@file_id, @file_type, @file_original_filename, @file_size, @bucket_name, @file_key, @standard_doc_number, "
													+ "@file_original_application, @file_original_author, @file_original_document_title, "
													+ "@file_original_create_datetime, @file_original_modified_datetime, @file_original_pdf_version, "
													+ "@parent_original_create_datetime, @parent_original_modified_datetime, @file_original_document_bookmark, "
													+ "@status, @create_datetime, @edit_datetime) "
													+ "ON CONFLICT DO NOTHING";

							cmd.Parameters.AddWithValue("file_id", fileId);
							cmd.Parameters.AddWithValue("file_type", file.file_type);
							cmd.Parameters.AddWithValue("file_original_filename", file.file_original_filename ?? "");
							cmd.Parameters.AddWithValue("file_size", file.file_size);
							cmd.Parameters.AddWithValue("bucket_name", file.bucket_name ?? "");
							cmd.Parameters.AddWithValue("file_key", file.file_key);
							cmd.Parameters.AddWithValue("standard_doc_number", file.standard_doc_number ?? "");
							cmd.Parameters.AddWithValue("status", file.status ?? "active");
							cmd.Parameters.AddWithValue("file_original_application", file.file_original_application ?? "");
							cmd.Parameters.AddWithValue("file_original_author", file.file_original_author ?? "");
							cmd.Parameters.AddWithValue("file_original_document_title", file.file_original_document_title ?? "");
							cmd.Parameters.AddWithValue(
								"file_original_create_datetime",
								file.file_original_create_datetime != null
								? (object)DateTimeHelper.ConvertToUTCDateTime(file.file_original_create_datetime) : DBNull.Value);
							cmd.Parameters.AddWithValue(
								"file_original_modified_datetime",
								file.file_original_modified_datetime != null
								? (object)DateTimeHelper.ConvertToUTCDateTime(file.file_original_modified_datetime) : DBNull.Value);
							cmd.Parameters.AddWithValue("file_original_pdf_version", file.file_original_pdf_version ?? "");
							cmd.Parameters.AddWithValue(
								"parent_original_create_datetime",
								file.parent_original_create_datetime != null
								? (object)DateTimeHelper.ConvertToUTCDateTime(file.parent_original_create_datetime) : DBNull.Value);
							cmd.Parameters.AddWithValue(
								"parent_original_modified_datetime",
								file.parent_original_modified_datetime != null
								? (object)DateTimeHelper.ConvertToUTCDateTime(file.parent_original_modified_datetime) : DBNull.Value);
							cmd.Parameters.AddWithValue("file_original_document_bookmark", file.file_original_document_bookmark ?? "");
							cmd.Parameters.AddWithValue("create_datetime", timestamp);
							cmd.Parameters.AddWithValue("edit_datetime", timestamp);

							if (cmd.ExecuteNonQuery() > 0)
							{
								return Ok(new
								{
									file_id = fileId,
									status = "completed"
								});
							}
							else
							{
								return Ok(new
								{
									file_id = fileId,
									status = "duplicated"
								});
							}
						}
						else
						{
							return Ok(new
							{
								file_id = fileId,
								status = "duplicated"
							});
						}
					}
				}
				else
				{
					return BadRequest(new
					{
						status = missingParameter + " is required"
					});
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (isInternalRequest == false)
				{
					_dbHelper.CloseConnection();
				}
			}
		}


		[HttpPost]
		[Route("CreateDocumentFileDL")]
		public IActionResult Post(DLDocumentFile documentFile, bool isInternalRequest = false)
		{
			try
			{
				var missingParameter = documentFile.CheckRequiredParameters(new string[] { "file_id", "doc_id" });

				if (missingParameter == null)
				{
					var timestamp = DateTime.UtcNow;
					var docFileId = documentFile.doc_file_id ?? Guid.NewGuid().ToString();

					using (var cmd = _dbHelper.SpawnCommand())
					{

						cmd.CommandText = $"SELECT EXISTS (SELECT true FROM document_files WHERE doc_file_id='{docFileId}')";

						if ((bool)cmd.ExecuteScalar() == false)
						{
							cmd.CommandText = $"SELECT EXISTS (SELECT true FROM project_documents WHERE doc_id='{documentFile.doc_id}')";

							if ((bool)cmd.ExecuteScalar() == false)
							{
								return BadRequest(new { status = "doc_id doesn't exist" });
							}

							cmd.CommandText = $"SELECT EXISTS (SELECT true FROM files WHERE file_id='{documentFile.file_id}')";

							if ((bool)cmd.ExecuteScalar() == false)
							{
								return BadRequest(new { status = "file_id doesn't exist" });
							}

							cmd.CommandText = "INSERT INTO document_files (doc_file_id, file_id, "
													+ "doc_id, create_datetime, edit_datetime) "
													+ "VALUES(@doc_file_id, @file_id, @doc_id, "
													+ "@create_datetime, @edit_datetime) ON CONFLICT DO NOTHING";

							cmd.Parameters.AddWithValue("doc_file_id", docFileId);
							cmd.Parameters.AddWithValue("file_id", documentFile.file_id);
							cmd.Parameters.AddWithValue("doc_id", documentFile.doc_id);
							cmd.Parameters.AddWithValue("create_datetime", timestamp);
							cmd.Parameters.AddWithValue("edit_datetime", timestamp);

							cmd.ExecuteNonQuery();

							return Ok(new
							{
								document_file_id = docFileId,
								status = "completed"
							});
						}
						else
						{
							return Ok(new
							{
								document_file_id = docFileId,
								status = "duplicated"
							});
						}
					}
				}
				else
				{
					return BadRequest(new
					{
						status = missingParameter + " is required"
					});
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (isInternalRequest == false)
				{
					_dbHelper.CloseConnection();
				}
			}
		}


		[HttpGet]
		[Route("FindDrawingsBySheetNum")]
		public IActionResult Get(
			[FromQuery(Name = "project_id")] string project_id,
			[FromQuery(Name = "sheet_number")] string sheet_number,
			[FromQuery(Name = "sheet_subproject")] string sheet_subproject,
			[FromQuery(Name = "sheet_pagenumber")] string sheet_pagenumber)
		{
			try
			{
				// validation check
				if (project_id == null || sheet_number == null)
				{
					return BadRequest(new
					{
						status = "project_id and sheet_number are required"
					});
				}

				if (string.IsNullOrEmpty(sheet_subproject))
                {
					sheet_subproject = string.Empty;
                }

				if (string.IsNullOrEmpty(sheet_pagenumber))
                {
					sheet_pagenumber = string.Empty;
                }

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "SELECT project_documents.doc_id, project_documents.project_id, project_documents.doc_number, project_documents.doc_name, project_documents.doc_version, project_documents.doc_revision, "
													+ "project_documents.doc_next_rev, project_documents.status, project_documents.create_datetime, project_documents.edit_datetime, project_documents.submission_datetime, files.file_original_modified_datetime, project_documents.submission_id, "
													+ "files.parent_original_modified_datetime, files.file_type, project_documents.doc_subproject, project_documents.doc_pagenumber "
													+ "FROM project_documents LEFT OUTER JOIN document_files ON document_files.doc_id=project_documents.doc_id "
													+ "LEFT OUTER JOIN files ON files.file_id=document_files.file_id "
													+ "WHERE (files.file_type='source_system_original' OR files.file_type='enhanced_original') AND project_documents.project_id='" + project_id + "' AND project_documents.doc_number='" + sheet_number + "' ORDER BY project_documents.create_datetime";

					using (var reader = cmd.ExecuteReader())
					{
						var list = new List<Dictionary<string, string>>();

						while (reader.Read())
						{
							var record = new Dictionary<string, string>()
							{
								{ "doc_id", _dbHelper.SafeGetString(reader, 0) },
								{ "project_id", _dbHelper.SafeGetString(reader, 1) },
								{ "doc_number", _dbHelper.SafeGetString(reader, 2) },
								{ "doc_name", _dbHelper.SafeGetString(reader, 3) },
								{ "doc_version", _dbHelper.SafeGetString(reader, 4) },
								{ "doc_revision", _dbHelper.SafeGetString(reader, 5) },
								{ "doc_next_rev", _dbHelper.SafeGetString(reader, 6) },
								{ "status", _dbHelper.SafeGetString(reader, 7) },
								{ "create_datetime", _dbHelper.SafeGetDatetimeString(reader, 8) },
								{ "edit_datetime", _dbHelper.SafeGetDatetimeString(reader, 9) },
								{ "submission_datetime", _dbHelper.SafeGetDatetimeString(reader, 10) },
								{ "file_original_modified_datetime", _dbHelper.SafeGetDatetimeString(reader, 11) },
								{ "submission_id", _dbHelper.SafeGetString(reader, 12) },
								{ "parent_original_modified_datetime", _dbHelper.SafeGetDatetimeString(reader, 13) },
								{ "file_type", _dbHelper.SafeGetString(reader, 14) },
								{ "doc_subproject", _dbHelper.SafeGetString(reader, 15) },
								{ "doc_pagenumber", _dbHelper.SafeGetString(reader, 16) },
							};

							if (record["doc_subproject"] == sheet_subproject && record["doc_pagenumber"] == sheet_pagenumber)
                            {
								list.Add(record);
							}
						}

						reader.Close();

						var resultArray = new List<Dictionary<string, string>> { };

						list.ForEach(record =>
						{
							if (record["file_type"] == "source_system_original")
							{
								var enhancedDocumentRecord = list.Find(
									listItem => listItem["doc_id"] == record["doc_id"] && listItem["file_type"] == "enhanced_original");

								if (enhancedDocumentRecord != null)
								{
									resultArray.Add(enhancedDocumentRecord);
								}
								else
								{
									resultArray.Add(record);
								}
							}
						});
						return Ok(resultArray);
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpGet]
		[Route("GetDocument")]
		public IActionResult Get([FromQuery(Name = "doc_id")] string doc_id, bool meaning_less)
		{
			try
			{
				// validation check
				if (doc_id == null)
				{
					return BadRequest(new
					{
						status = "doc_id is required"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "SELECT project_documents.doc_id, project_documents.project_id, project_documents.doc_number, "
													+ "project_documents.doc_name, project_documents.doc_version, project_documents.doc_revision, "
													+ "project_documents.doc_next_rev, project_documents.status, project_documents.create_datetime, "
													+ "project_documents.edit_datetime, "
													+ "files.file_id, files.file_type, files.file_key, files.bucket_name, files.file_size, project_documents.submission_datetime, project_documents.project_doc_original_filename, "
													+ "project_documents.process_status, project_documents.doc_name_abbrv, project_documents.display_name, "
													+ "project_submissions.submission_name, project_submissions.submitter_email, "
													+ "project_documents.doc_desc, project_documents.doc_discipline, project_documents.doc_type, project_documents.submission_id, "
													+ "project_documents.doc_size, project_documents.doc_parent_id, project_documents.doc_pagenumber, project_documents.doc_subproject "
													+ "FROM project_documents LEFT OUTER JOIN document_files ON document_files.doc_id=project_documents.doc_id "
													+ "LEFT OUTER JOIN files ON  files.file_id=document_files.file_id "
													+ "LEFT OUTER JOIN project_submissions ON project_submissions.project_submission_id=project_documents.submission_id "
													+ "WHERE project_documents.doc_id='" + doc_id + "'";

					using (var reader = cmd.ExecuteReader())
					{
						var resultArray = new List<Dictionary<string, string>>();

						while (reader.Read())
						{
							var result = new Dictionary<string, string>()
							{
								{ "doc_id", _dbHelper.SafeGetString(reader, 0) },
								{ "project_id", _dbHelper.SafeGetString(reader, 1) },
								{ "doc_number", _dbHelper.SafeGetString(reader, 2) },
								{ "doc_name", _dbHelper.SafeGetString(reader, 3) },
								{ "doc_version", _dbHelper.SafeGetString(reader, 4) },
								{ "doc_revision", _dbHelper.SafeGetString(reader, 5) },
								{ "doc_next_rev", _dbHelper.SafeGetString(reader, 6) },
								{ "status", _dbHelper.SafeGetString(reader, 7) },
								{ "create_datetime", _dbHelper.SafeGetDatetimeString(reader, 8) },
								{ "edit_datetime", _dbHelper.SafeGetDatetimeString(reader, 9) },
								{ "file_id", _dbHelper.SafeGetString(reader, 10) },
								{ "file_type", _dbHelper.SafeGetString(reader, 11) },
								{ "file_key", _dbHelper.SafeGetString(reader, 12) },
								{ "bucket_name", _dbHelper.SafeGetString(reader, 13) },
								{ "file_size", _dbHelper.SafeGetString(reader, 14) },
								{ "submission_datetime", _dbHelper.SafeGetDatetimeString(reader, 15) },
								{ "project_doc_original_filename", _dbHelper.SafeGetString(reader, 16) },
								{ "process_status", _dbHelper.SafeGetString(reader, 17) },
								{ "doc_name_abbrv", _dbHelper.SafeGetString(reader, 18) },
								{ "display_name", _dbHelper.SafeGetString(reader, 19) },
								{ "submission_name", _dbHelper.SafeGetString(reader, 20) },
								{ "submitter_email", _dbHelper.SafeGetString(reader, 21) },
								{ "doc_desc", _dbHelper.SafeGetString(reader, 22) },
								{ "doc_discipline", _dbHelper.SafeGetString(reader, 23) },
								{ "doc_type", _dbHelper.SafeGetString(reader, 24) },
								{ "submission_id", _dbHelper.SafeGetString(reader, 25) },
								{ "doc_size", _dbHelper.SafeGetString(reader, 26) },
								{ "doc_parent_id", _dbHelper.SafeGetString(reader, 27) },
								{ "doc_pagenumber", _dbHelper.SafeGetString(reader, 28) },
								{ "doc_subproject", _dbHelper.SafeGetString(reader, 29) },
							};
							resultArray.Add(result);
						}
						reader.Close();
						return Ok(resultArray);
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpGet]
		[Route("FindProjectDocuments")]
		public IActionResult Get(ProjectDocumentFindRequest request)
		{
			try
			{
				var detailLevel = request.detail_level ?? "basic";

				using (var cmd = _dbHelper.SpawnCommand())
				{
					var whereString = " WHERE files.file_type='source_system_original' AND ";
					whereString += request.project_id != null ? $"project_documents.project_id='{request.project_id}' AND " : "";
					whereString += request.submission_id != null ? $"project_documents.submission_id='{request.submission_id}' AND " : "";
					whereString += request.customer_id != null ? $"projects.project_customer_id='{request.customer_id}' AND " : "";
					whereString += request.doc_number != null ? $"project_documents.doc_number='{request.doc_number}' AND " : "";
					whereString += request.doc_parent_id != null ? $"project_documents.doc_parent_id='{request.doc_parent_id}' AND " : "";
					whereString += request.latest_rev_only ? "project_documents.doc_next_rev IS NULL AND " : "";
					whereString += request.doc_type != null ? $"project_documents.doc_type LIKE '%{request.doc_type}%' AND " : "";
                    whereString += request.doc_size != null ? $"project_documents.doc_size='{request.doc_size}' AND " : "";
                    whereString = whereString.Remove(whereString.Length - 5);

					if (detailLevel == "compact")
					{
						cmd.CommandText = "SELECT files.bucket_name, project_documents.display_name, "
							+ "project_documents.doc_id, project_documents.doc_next_rev, project_documents.doc_sequence, "
							+ "files.file_id, project_documents.status, project_documents.submission_datetime "
							+ "FROM project_documents "
							+ "LEFT JOIN document_files ON project_documents.doc_id=document_files.doc_id "
							+ "LEFT JOIN files on document_files.file_id=files.file_id "
							+ whereString;

						using (var reader = cmd.ExecuteReader())
						{
							var resultList = new List<Dictionary<string, string>>();
							while (reader.Read())
							{
								var result = new Dictionary<string, string>
								{
									["bucket_name"] = _dbHelper.SafeGetString(reader, 0),
									["display_name"] = _dbHelper.SafeGetString(reader, 1),
									["doc_id"] = _dbHelper.SafeGetString(reader, 2),
									["doc_next_rev"] = _dbHelper.SafeGetString(reader, 3),
									["doc_sequence"] = _dbHelper.SafeGetInteger(reader, 4),
									["file_id"] = _dbHelper.SafeGetString(reader, 5),
									["status"] = _dbHelper.SafeGetString(reader, 6),
									["submission_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 7)
								};

								resultList.Add(result);
							}
							return Ok(resultList);
						}
					}

					cmd.CommandText = "SELECT project_documents.doc_discipline, project_documents.doc_id, project_documents.doc_name, "
													+ "project_documents.doc_number, project_documents.doc_revision, project_documents.doc_next_rev, "
													+ "files.file_id, files.file_size, project_documents.project_id, projects.project_name, "
													+ "project_documents.status, project_documents.submission_datetime, project_documents.doc_type, "
													+ "project_documents.create_datetime, customers.customer_id, customers.customer_name, project_documents.doc_desc, "
													+ "project_documents.edit_datetime, projects.project_admin_user_id, project_documents.doc_parent_id, project_documents.submission_id, "
													+ "project_documents.create_user_id, project_documents.edit_user_id, project_documents.project_doc_original_filename, "
													+ "project_documents.process_status, project_documents.doc_name_abbrv, project_documents.display_name, "
													+ "project_documents.doc_size, "
                                                    + "files.bucket_name, project_documents.doc_pagenumber, "
                                                    + "project_documents.doc_sequence, project_documents.doc_subproject "
                                                    + " FROM project_documents "
													+ "LEFT JOIN document_files ON project_documents.doc_id=document_files.doc_id "
													+ "LEFT JOIN files ON document_files.file_id=files.file_id "
													+ "LEFT JOIN projects ON project_documents.project_id=projects.project_id "
													+ "LEFT JOIN project_submissions ON project_submissions.project_submission_id=project_documents.submission_id "
													+ "LEFT JOIN customers ON projects.project_customer_id=customers.customer_id "
													+ whereString;

					using (var reader = cmd.ExecuteReader())
					{
						var resultList = new List<Dictionary<string, string>>();

						while (reader.Read())
						{
							var result = new Dictionary<string, string>
							{
								["doc_discipline"] = _dbHelper.SafeGetString(reader, 0),
								["doc_id"] = _dbHelper.SafeGetString(reader, 1),
								["doc_name"] = _dbHelper.SafeGetString(reader, 2),
								["doc_number"] = _dbHelper.SafeGetString(reader, 3),
								["doc_revision"] = _dbHelper.SafeGetString(reader, 4),
								["doc_next_rev"] = _dbHelper.SafeGetString(reader, 5),
								["file_id"] = _dbHelper.SafeGetString(reader, 6),
								["file_size"] = _dbHelper.SafeGetString(reader, 7),
								["project_id"] = _dbHelper.SafeGetString(reader, 8),
								["project_name"] = _dbHelper.SafeGetString(reader, 9),
								["status"] = _dbHelper.SafeGetString(reader, 10),
								["submission_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 11),
								["doc_type"] = _dbHelper.SafeGetString(reader, 12),
								["project_doc_original_filename"] = _dbHelper.SafeGetString(reader, 23),
								["process_status"] = _dbHelper.SafeGetString(reader, 24),
								["doc_name_abbrv"] = _dbHelper.SafeGetString(reader, 25),
								["display_name"] = _dbHelper.SafeGetString(reader, 26),
								["doc_size"] = _dbHelper.SafeGetString(reader, 27),

								["bucket_name"] = _dbHelper.SafeGetString(reader, 28),
								["doc_pagenumber"] = _dbHelper.SafeGetString(reader, 29),
								["doc_sequence"] = _dbHelper.SafeGetInteger(reader, 30),
								["doc_subproject"] = _dbHelper.SafeGetString(reader, 31)

							};

							if (detailLevel == "all" || detailLevel == "admin")
							{
								result["create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 13);
								result["customer_id"] = _dbHelper.SafeGetString(reader, 14);
								result["customer_name"] = _dbHelper.SafeGetString(reader, 15);
								result["doc_desc"] = _dbHelper.SafeGetString(reader, 16);
								result["edit_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 17);
								result["project_admin_user_id"] = _dbHelper.SafeGetString(reader, 18);
								result["doc_parent_id"] = _dbHelper.SafeGetString(reader, 19);
								result["submission_id"] = _dbHelper.SafeGetString(reader, 20);
							}

							if (detailLevel == "admin")
							{
								result["create_user_id"] = _dbHelper.SafeGetString(reader, 21);
								result["edit_user_id"] = _dbHelper.SafeGetString(reader, 22);
							}

							resultList.Add(result);
						}
						return Ok(resultList);
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpGet]
		[Route("GetDocumentRevisions")]
		public IActionResult Get(DocumentRevisionGetRequest request)
		{
			try
			{
				if (string.IsNullOrEmpty(request.doc_id))
				{
					return BadRequest(new
					{
						status = "Please provide doc_id"
					});
				}

				var currentDocumentDetail = __getDocumentDetail(request.doc_id);

				if (currentDocumentDetail == null)
				{
					return BadRequest(new
					{
						status = "Cannot find document with the specified doc_id"
					});
				}
				else
				{
					var resultList = new List<Dictionary<string, dynamic>> { currentDocumentDetail };
					var currentDocId = request.doc_id;

					while (true)
					{
						currentDocId = _documentManagementService.GetNextRevisionDocId(_dbHelper, currentDocId);

						if (string.IsNullOrEmpty(currentDocId))
						{
							break;
						}
						else
						{
							resultList.Add(__getDocumentDetail(currentDocId));
						}
					}

					currentDocId = request.doc_id;

					while (true)
					{
						currentDocId = _documentManagementService.GetPreviousRevisionDocId(_dbHelper, currentDocId);

						if (string.IsNullOrEmpty(currentDocId))
						{
							break;
						}
						else
						{
							resultList.Insert(0, __getDocumentDetail(currentDocId));
						}
					}

					for (var index = 0; index < resultList.Count; index++)
					{
						if (index != 0)
						{
							resultList[index]["doc_prev_rev"] = resultList[index - 1]["doc_id"];
						}

						resultList[index]["current_revision"] = (index == resultList.Count - 1);
						resultList[index]["revision_sequence_num"] = index;
					}
					return Ok(resultList);
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}

		[HttpPost]
		[Route("DeleteProjectDocument")]
		public IActionResult Post(ProjectDocumentDeleteRequest request, bool single = true)
		{
			try
			{
				if (string.IsNullOrEmpty(request.doc_id))
				{
					return BadRequest(new
					{
						status = "Please provide doc_id"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
                    cmd.CommandText = $"DELETE FROM project_folder_contents WHERE doc_id='{request.doc_id}'";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = $"DELETE FROM project_documents WHERE doc_id='{request.doc_id}'";
					cmd.ExecuteNonQuery();

					cmd.CommandText = $"DELETE FROM document_files WHERE doc_id='{request.doc_id}'";
					cmd.ExecuteNonQuery();

					return Ok(new
					{
						status = "completed"
					});
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("DeleteDocuments")]
		public IActionResult Post(ProjectDocumentDeleteRequest request)
		{
			try
			{
				if (string.IsNullOrEmpty(request.submission_id))
				{
					return BadRequest(new
					{
						status = "Please provide submission_id"
					});
				}

				var deletedDocumentCount = 0;
				var deletedDocumentFileCount = 0;
				var deletedFileCount = 0;
				var deletedFolderContentCount = 0;

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "DELETE FROM project_folder_contents WHERE doc_id "
							+ $"IN (SELECT doc_id FROM project_documents WHERE submission_id='{request.submission_id}')";
					deletedFolderContentCount = cmd.ExecuteNonQuery();
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "DELETE FROM files WHERE file_id "
													+ "IN (SELECT file_id FROM document_files WHERE doc_id "
													+ $"IN (SELECT doc_id FROM project_documents WHERE submission_id='{request.submission_id}'))"
													+ "AND file_id NOT IN (SELECT file_id FROM document_files WHERE doc_id "
													+ $"IN (SELECT doc_id FROM project_documents WHERE submission_id <> '{request.submission_id}'))";
					deletedFileCount = cmd.ExecuteNonQuery();
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "DELETE FROM document_files WHERE doc_id "
													+ $"IN (SELECT doc_id FROM project_documents WHERE submission_id='{request.submission_id}')";
					deletedDocumentFileCount = cmd.ExecuteNonQuery();
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = $"DELETE FROM project_documents WHERE submission_id='{request.submission_id}'";
					deletedDocumentCount = cmd.ExecuteNonQuery();
				}

				return Ok(new
				{
					status = $"Deleted {deletedDocumentCount} project_documents, {deletedDocumentFileCount} document_files, {deletedFileCount} files, {deletedFolderContentCount} folder contents"
				});
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("RemoveFolderContentDL")]
		public IActionResult Post(DLFolderContentDeleteRequest request, bool isInternalRequest = false)
		{
			try
			{
				var folderContentId = request.folder_content_id ?? "";

				if (string.IsNullOrEmpty(folderContentId))
				{
					// check missing parameter
					var missingParameter = request.CheckRequiredParameters(new string[] { "project_id", "folder_type", "doc_id" });

					if (missingParameter != null)
					{
						return BadRequest(new
						{
							status = $"{missingParameter} is required"
						});
					}

					var folderID = __getRootFolderId(request.project_id, request.folder_type);

					if (string.IsNullOrEmpty(folderID))
					{
						return Ok(new
						{
							status = "No need to delete, root folder doesn't exist"
						});
					}

					if (!string.IsNullOrEmpty(request.folder_path))
					{
						var pathNames = request.folder_path.Split("/");

						for (var index = 0; index < pathNames.Length; index++)
						{
							folderID = __checkFolderNameExists(request.project_id, folderID, pathNames[index]);

							if (string.IsNullOrEmpty(folderID))
							{
								return Ok(new
								{
									status = $"No need to delete, sub folder <{pathNames[index]}> doesn't exist"
								});
							}
						}
					}

					folderContentId = __getFolderContentId(folderID, request.doc_id);
				}


				if (string.IsNullOrEmpty(folderContentId))
				{
					return Ok(new
					{
						status = "Content doesn't exist"
					});
				}
				else
				{
					__deleteFolderContent(folderContentId, false);
					_documentManagementService.CreateFolderTransactionLog(
						_dbHelper, __getFolderContent(folderContentId), "remove_file");

					return Ok(new
					{
						status = "completed"
					});
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				if (!isInternalRequest)
                {
					_dbHelper.CloseConnection();
				}
			}
		}


		[HttpGet]
		[Route("GetDocumentDetails")]
		public IActionResult Get(DocumentDetailsGetRequest request)
		{
			try
			{
				if (string.IsNullOrEmpty(request.doc_id))
				{
					return BadRequest(new
					{
						status = "Please provide doc_id"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "SELECT project_documents.create_datetime, project_documents.edit_datetime, project_documents.doc_id, "
													+ "project_documents.doc_name, project_documents.doc_number, project_documents.doc_revision, project_documents.display_name, files.file_original_filename, files.file_original_create_datetime, "
													+ "files.file_original_modified_datetime, project_documents.status, project_submissions.submission_name, project_submissions.submitter_email, "
													+ "project_documents.submission_datetime, files.bucket_name, files.file_key, project_documents.doc_next_rev, "
													+ "project_documents.doc_size, files.file_id, "
													+ "project_documents.doc_subproject, project_documents.doc_pagenumber, project_documents.doc_version "
													+ "FROM project_documents "
													+ "LEFT JOIN document_files ON project_documents.doc_id=document_files.doc_id "
													+ "LEFT JOIN files ON files.file_id=document_files.file_id "
													+ "LEFT JOIN project_submissions ON project_submissions.project_submission_id=project_documents.submission_id "
													+ $"WHERE project_documents.doc_id='{request.doc_id}' AND files.file_type='source_system_original'";

					using (var reader = cmd.ExecuteReader())
					{
						if (reader.Read())
						{
							var result = new Dictionary<string, object>
							{
								["create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 0),
								["edit_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 1),
								["doc_id"] = _dbHelper.SafeGetString(reader, 2),
								["doc_name"] = _dbHelper.SafeGetString(reader, 3),
								["doc_number"] = _dbHelper.SafeGetString(reader, 4),
								["doc_revision"] = _dbHelper.SafeGetString(reader, 5),
								["display_name"] = _dbHelper.SafeGetString(reader, 6),
								["file_original_filename"] = _dbHelper.SafeGetString(reader, 7),
								["file_original_create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 8),
								["file_original_modified_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 9),
								["status"] = _dbHelper.SafeGetString(reader, 10),
								["submission_name"] = _dbHelper.SafeGetString(reader, 11),
								["submitter_email"] = _dbHelper.SafeGetString(reader, 12),
								["submission_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 13),
								["bucket_name"] = _dbHelper.SafeGetString(reader, 14),
								["file_key"] = _dbHelper.SafeGetString(reader, 15),
								["doc_next_rev"] = _dbHelper.SafeGetString(reader, 16),
								["doc_size"] = _dbHelper.SafeGetString(reader, 17),
								["file_id"] = _dbHelper.SafeGetString(reader, 18),
								["doc_subproject"] = _dbHelper.SafeGetString(reader, 19),
								["doc_pagenumber"] = _dbHelper.SafeGetString(reader, 20),
								["doc_version"] = _dbHelper.SafeGetString(reader, 21),
							};

							reader.Close();

							result["revisions"] = _documentManagementService.GetDocumentRevisions(_dbHelper, request.doc_id);
							result["comparisons"] = _documentManagementService.GetDocumentComparisons(_dbHelper, (result["revisions"] as List<Dictionary<string, string>>));
							return Ok(result);
						}
						else
						{
							return BadRequest(new
							{
								status = "Failed to find document detail!"
							});
						}
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}

		[HttpGet]
		[Route("FindComparisonDrawings")]
		public IActionResult Get(ComparisonDrawingFindRequest request)
		{
			try
			{
				if (string.IsNullOrEmpty(request.project_id))
				{
					return BadRequest(new
					{
						status = "Please provide project_id"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "SELECT files.file_id, project_documents.doc_id, project_documents.doc_name, "
							+ "project_documents.doc_number, project_documents.doc_revision, project_documents.display_name, "
							+ "files.file_key, files.bucket_name "
							+ "FROM project_documents "
							+ "LEFT JOIN document_files ON document_files.doc_id=project_documents.doc_id "
							+ "LEFT JOIN files ON files.file_id=document_files.file_id "
							+ $"WHERE project_documents.project_id='{request.project_id}' AND files.file_type='comparison_file' ORDER BY project_documents.doc_number, files.create_datetime DESC";

					using (var reader = cmd.ExecuteReader())
					{
						var result = new List<Dictionary<string, string>> { };

						while (reader.Read())
						{
                            // Ignore previously generated comparison, only fetch the latest one
                            var alreadyAdded = result.Find(item =>
                            {
                                return item["doc_id"] == _dbHelper.SafeGetString(reader, 1);
                            });

                            if (alreadyAdded == null)
                            {
                                result.Add(new Dictionary<string, string>
                                {
                                    ["file_id"] = _dbHelper.SafeGetString(reader, 0),
                                    ["doc_id"] = _dbHelper.SafeGetString(reader, 1),
                                    ["doc_name"] = _dbHelper.SafeGetString(reader, 2),
                                    ["doc_number"] = _dbHelper.SafeGetString(reader, 3),
                                    ["doc_revision"] = _dbHelper.SafeGetString(reader, 4),
                                    ["display_name"] = _dbHelper.SafeGetString(reader, 5),
                                    ["file_key"] = _dbHelper.SafeGetString(reader, 6),
                                    ["bucket_name"] = _dbHelper.SafeGetString(reader, 7),
                                });
                            }
						}
						return Ok(result);
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}

		[HttpGet]
		[Route("FindDuplicateFileNamesCount")]
		public IActionResult Get(DuplicateNameFindRequest request)
		{
			try
			{
				var missingParameter = request.CheckRequiredParameters(new string[] { "project_id", "original_filename" });

				if (missingParameter != null)
				{
					return BadRequest(new
					{
						status = missingParameter + " is required"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "SELECT COUNT(project_documents.doc_id) FROM project_documents "
							+ "INNER JOIN project_folder_contents ON project_documents.doc_id=project_folder_contents.doc_id "
							+ "INNER JOIN project_folders ON project_folders.folder_id=project_folder_contents.folder_id "
							+ $"WHERE project_documents.project_id='{request.project_id}' AND project_documents.project_doc_original_filename='{request.original_filename}' AND "
							+ $"project_folder_contents.folder_path='{request.folder_path}' AND project_folders.folder_type='source_current'";

					using (var reader = cmd.ExecuteReader())
					{
						if (reader.Read())
						{
							return Ok(new
							{
								count = _dbHelper.SafeGetIntegerRaw(reader, 0)
							});
						}
						else
						{
							return BadRequest(new
							{
								status = "Failed to get count"
							});
						}
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}

		[HttpPost]
		[Route("UpdateFolder")]
		public IActionResult Post(FolderUpdateRequest request)
		{
			try
			{
				if (string.IsNullOrEmpty(request.search_folder_id))
				{
					return BadRequest(new
					{
						status = "Please provide search_folder_id"
					});
				}

				if (string.IsNullOrEmpty(request.folder_name) && string.IsNullOrEmpty(request.status))
				{
					return BadRequest(new
					{
						status = "Please provide update parameter"
					});
				}

				var projectID = __getProjectIdFromFolderId(request.search_folder_id);
				var folderName = __getFolderNameFromFolderId(request.search_folder_id);

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "UPDATE project_folders SET "
							+ "folder_name = COALESCE(@folder_name, folder_name),"
							+ "status = COALESCE(@status, status),"
							+ "edit_datetime = @edit_datetime "
							+ $"WHERE folder_id='{request.search_folder_id}'";

					cmd.Parameters.AddWithValue("folder_name", (object)request.folder_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("status", (object)request.status ?? DBNull.Value);
					cmd.Parameters.AddWithValue("edit_datetime", DateTime.UtcNow);

					var updatedCount = cmd.ExecuteNonQuery();

					if (updatedCount == 0)
					{
						return BadRequest(new
						{
							status = "folder_id not found"
						});
					}
				}

				if (!string.IsNullOrEmpty(request.folder_name))
				{
					_documentManagementService.CreateFolderTransactionLog(
						_dbHelper,
						new Dictionary<string, object>
						{
							{ "project_id", projectID },
							{ "folder_id", request.search_folder_id },
							{ "original_folder_name", folderName },
							{ "new_folder_name", request.folder_name },
							{ "doc_id", "" },
							{ "file_id", "" },
							{ "folder_path", "" },
						},
						"rename_folder");
				}
				else if (request.status == "inactive" || request.status == "deleted")
				{
					_documentManagementService.CreateFolderTransactionLog(
						_dbHelper,
						new Dictionary<string, object>
						{
							{ "project_id", projectID },
							{ "folder_id", request.search_folder_id },
							{ "original_folder_name", folderName },
							{ "new_folder_name", folderName },
							{ "doc_id", "" },
							{ "file_id", "" },
							{ "folder_path", "" },
						},
						"remove_folder");
				}

				return Ok(new
				{
					status = "updated"
				});
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpGet]
		[Route("FindSourceFiles")]
		public IActionResult Get(SourceFilesFindRequest request)
		{
			try
			{
				if (string.IsNullOrEmpty(request.project_id))
				{
					return BadRequest(new { status = "Please provide project_id" });
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "SELECT files.bucket_name, files.file_key, project_documents.display_name, project_documents.doc_discipline, project_documents.doc_id, "
							+ "project_documents.doc_name, project_documents.doc_next_rev, project_documents.doc_number, project_documents.doc_revision, project_documents.doc_type, "
							+ "files.file_id, files.file_size, project_folder_contents.folder_path, project_folders.folder_type, project_documents.project_doc_original_filename, "
							+ "projects.project_name, project_documents.status, project_documents.create_datetime, project_documents.edit_datetime, projects.project_admin_user_id, "
							+ "project_documents.doc_parent_id, project_documents.submission_datetime, project_documents.submission_id, "
							+ "project_documents.create_user_id, project_documents.edit_user_id "
							+ "FROM projects "
							+ "LEFT JOIN project_documents ON projects.project_id=project_documents.project_id "
							+ "LEFT JOIN project_folder_contents ON project_folder_contents.doc_id=project_documents.doc_id "
							+ "LEFT JOIN project_folders ON project_folder_contents.folder_id=project_folders.folder_id "
							+ "LEFT JOIN files ON project_folder_contents.file_id=files.file_id "
							+ $"WHERE projects.project_id='{request.project_id}' AND project_documents.doc_type LIKE 'original_%' AND project_folders.folder_type='source_current'";

					using (var reader = cmd.ExecuteReader())
					{
						var result = new List<Dictionary<string, string>> { };

						while (reader.Read())
						{
							var dict = new Dictionary<string, string>
							{
								{ "bucket_name", _dbHelper.SafeGetString(reader, 0) },
								{ "file_key", _dbHelper.SafeGetString(reader, 1) },
								{ "display_name", _dbHelper.SafeGetString(reader, 2) },
								{ "doc_discipline", _dbHelper.SafeGetString(reader, 3) },
								{ "doc_id", _dbHelper.SafeGetString(reader, 4) },
								{ "doc_name", _dbHelper.SafeGetString(reader, 5) },
								{ "doc_next_rev", _dbHelper.SafeGetString(reader, 6) },
								{ "doc_number", _dbHelper.SafeGetString(reader, 7) },
								{ "doc_revision", _dbHelper.SafeGetString(reader, 8) },
								{ "doc_type", _dbHelper.SafeGetString(reader, 9) },
								{ "file_id", _dbHelper.SafeGetString(reader, 10) },
								{ "file_size", _dbHelper.SafeGetString(reader, 11) },
								{ "folder_path", _dbHelper.SafeGetString(reader, 12) },
								{ "folder_type", _dbHelper.SafeGetString(reader, 13) },
								{ "project_doc_original_filename", _dbHelper.SafeGetString(reader, 14) },
								{ "project_name", _dbHelper.SafeGetString(reader, 15) },
								{ "status", _dbHelper.SafeGetString(reader, 16) },
							};

							if (request.detail_level == "admin" || request.detail_level == "all")
							{
								dict["create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 17);
								dict["edit_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 18);
								dict["project_admin_user_id"] = _dbHelper.SafeGetString(reader, 19);
								dict["doc_parent_id"] = _dbHelper.SafeGetString(reader, 20);
								dict["submission_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 21);
								dict["submission_id"] = _dbHelper.SafeGetString(reader, 22);
							}

							if (request.detail_level == "admin")
							{
								dict["create_user_id"] = _dbHelper.SafeGetString(reader, 23);
								dict["edit_user_id"] = _dbHelper.SafeGetString(reader, 24);
							}

							result.Add(dict);
						}
						return Ok(result);
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}

		private Dictionary<string, dynamic> __getDocumentDetail(string docId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				var result = new Dictionary<string, dynamic> { };

				cmd.CommandText = "SELECT project_documents.create_datetime, doc_revision, doc_name, doc_next_rev, doc_number, doc_version, project_documents.edit_datetime, "
												+ "project_documents.status, project_submissions.submitter_email, project_submissions.project_submission_id, project_submissions.submission_name, "
												+ "submission_datetime, doc_parent_id, project_doc_original_filename, display_name "
												+ "FROM project_documents LEFT OUTER JOIN project_submissions ON project_documents.submission_id=project_submissions.project_submission_id "
												+ "WHERE project_documents.doc_id='" + docId + "'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						result = new Dictionary<string, dynamic>
						{
							["create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 0),
							["doc_revision"] = _dbHelper.SafeGetString(reader, 1),
							["doc_name"] = _dbHelper.SafeGetString(reader, 2),
							["doc_next_rev"] = _dbHelper.SafeGetString(reader, 3),
							["doc_number"] = _dbHelper.SafeGetString(reader, 4),
							["doc_version"] = _dbHelper.SafeGetString(reader, 5),
							["edit_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 6),
							["status"] = _dbHelper.SafeGetString(reader, 7),
							["submitter_email"] = _dbHelper.SafeGetString(reader, 8),
							["submission_id"] = _dbHelper.SafeGetString(reader, 9),
							["submission_name"] = _dbHelper.SafeGetString(reader, 10),
							["submission_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 11),
							["doc_parent_id"] = _dbHelper.SafeGetString(reader, 12),
							["project_doc_original_filename"] = _dbHelper.SafeGetString(reader, 13),
							["display_name"] = _dbHelper.SafeGetString(reader, 14)
						};
					}
					else
					{
						return null;
					}
				}

				var files = new List<Dictionary<string, string>> { };

				cmd.CommandText = "SELECT bucket_name, file_key, file_original_create_datetime, file_original_modified_datetime, "
												+ "parent_original_create_datetime, parent_original_modified_datetime, file_type, files.file_id "
												+ "FROM files LEFT OUTER JOIN document_files ON files.file_id=document_files.file_id "
												+ "WHERE document_files.doc_id='" + docId + "' ORDER BY files.create_datetime desc";

				using (var reader = cmd.ExecuteReader())
				{
					while (reader.Read())
					{
						files.Add(new Dictionary<string, string>
						{
							["bucket_name"] = _dbHelper.SafeGetString(reader, 0),
							["file_key"] = _dbHelper.SafeGetString(reader, 1),
							["file_original_create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 2),
							["file_original_modified_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 3),
							["parent_original_create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 4),
							["parent_original_modified_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 5),
							["file_type"] = _dbHelper.SafeGetString(reader, 6),
							["file_id"] = _dbHelper.SafeGetString(reader, 7),
						});
					}
				}

				var originalFile = files.Find(file => file["file_type"] == "source_system_original");
				var rasterFile = files.Find(file => file["file_type"] == "png_raster_file");
				var comparisonFile = files.Find(file => file["file_type"] == "comparison_file");

				if (originalFile != null)
				{
					result["bucket_original_file"] = originalFile["bucket_name"];
					result["file_key_original"] = originalFile["file_key"];
					result["file_original_create_datetime"] = originalFile["file_original_create_datetime"];
					result["file_original_modified_datetime"] = originalFile["file_original_modified_datetime"];
					result["parent_original_create_datetime"] = originalFile["parent_original_create_datetime"];
					result["parent_original_modified_datetime"] = originalFile["parent_original_modified_datetime"];
					result["file_id"] = originalFile["file_id"];
				}

				if (comparisonFile != null)
				{
					result["bucket_comparison_file"] = comparisonFile["bucket_name"];
					result["file_key_comparison"] = comparisonFile["file_key"];
				}

				if (rasterFile != null)
				{
					result["bucket_raster_file"] = rasterFile["bucket_name"];
					result["file_key_raster"] = rasterFile["file_key"];
				}

				if (!string.IsNullOrEmpty(result["doc_parent_id"]))
				{
					cmd.CommandText = "SELECT file_key, bucket_name FROM files "
											+ "LEFT OUTER JOIN document_files ON files.file_id=document_files.file_id "
											+ $"WHERE document_files.doc_id='{result["doc_parent_id"]}' AND files.file_type='source_system_original'";

					using (var reader = cmd.ExecuteReader())
					{
						if (reader.Read())
						{
							result["file_key_parent"] = _dbHelper.SafeGetString(reader, 0);
							result["bucket_parent_file"] = _dbHelper.SafeGetString(reader, 1);
						}
					}
				}

				result["doc_id"] = docId;

				return result;
			}
		}

		private bool __checkFolderExists(string folderId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT EXISTS (SELECT true FROM project_folders WHERE folder_id='" + folderId + "');";
				return (bool)cmd.ExecuteScalar();
			}
		}

		private string __checkFolderNameExists(string projectId, string parentFolderId, string folderName)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT folder_id FROM project_folders WHERE project_id=@project_id AND folder_name=@folder_name AND parent_folder_id=@parent_folder_id;";
				cmd.Parameters.AddWithValue("project_id", projectId);
				cmd.Parameters.AddWithValue("folder_name", folderName);
				cmd.Parameters.AddWithValue("parent_folder_id", parentFolderId ?? "");

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						var value = _dbHelper.SafeGetString(reader, 0);
						reader.Close();

						return value;
					}
					else
					{
						reader.Close();
						return null;
					}
				}
			}
		}

		private string __createFolder(
			string projectId,
			string folderId,
			string folderName,
			string parentFolderId,
			string folder_type,
			string status,
			DateTime timestamp,
			string submission_id = "")
		{
			using (var newCmd = _dbHelper.SpawnCommand())
			{
				var folderType = folder_type;

				// Retrieve parent's folder type
				if (!string.IsNullOrEmpty(parentFolderId))
				{
					newCmd.CommandText = "SELECT folder_type FROM project_folders WHERE folder_id='" + parentFolderId + "';";
					using (var reader = newCmd.ExecuteReader())
					{
						if (reader.Read())
						{
							folderType = _dbHelper.SafeGetString(reader, 0);
						}
					}
				}

                // Create folder
                newCmd.CommandText = "INSERT INTO project_folders (project_id, folder_id, "
								+ "folder_name, parent_folder_id, folder_type, status, create_datetime, "
								+ "edit_datetime, folder_content_quantity, submission_id) "
								+ "VALUES(@project_id, @folder_id, @folder_name, @parent_folder_id, @folder_type, "
								+ "@status, @create_datetime, @edit_datetime, @folder_content_quantity, @submission_id) "
                                + "ON CONFLICT(project_id, parent_folder_id, folder_name) DO NOTHING;";

				newCmd.Parameters.AddWithValue("project_id", projectId);
				newCmd.Parameters.AddWithValue("folder_id", folderId);
				newCmd.Parameters.AddWithValue("folder_name", folderName);
				newCmd.Parameters.AddWithValue("parent_folder_id", parentFolderId ?? "");
				newCmd.Parameters.AddWithValue("folder_type", folderType);
				newCmd.Parameters.AddWithValue("status", status);
				newCmd.Parameters.AddWithValue("create_datetime", timestamp);
				newCmd.Parameters.AddWithValue("edit_datetime", timestamp);
				newCmd.Parameters.AddWithValue("folder_content_quantity", 0);
				newCmd.Parameters.AddWithValue("submission_id", submission_id);

				newCmd.ExecuteNonQuery();

                newCmd.CommandText = $"SELECT folder_id FROM project_folders WHERE project_id='{projectId}' AND parent_folder_id='{parentFolderId ?? ""}' AND folder_name='{folderName}';";

                using (var reader = newCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        folderId = _dbHelper.SafeGetString(reader, 0);
                    }
                }

				// Increase content quantity of parent folder 
				if (!string.IsNullOrEmpty(parentFolderId))
				{
					newCmd.CommandText = "UPDATE project_folders SET folder_content_quantity = folder_content_quantity + 1 "
														 + $"WHERE folder_id='{parentFolderId}';";
					newCmd.ExecuteNonQuery();
				}

                return folderId;
			}
		}

		private bool __checkFileIdExists(string fileId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = $"SELECT EXISTS (SELECT true FROM files WHERE file_id='{fileId}')";
				return (bool)cmd.ExecuteScalar();
			}
		}

		private int __copyFiles(string fileId, string docId)
		{
			var docFileIds = new List<string>();

			try
			{
				using (var cmd = _dbHelper.SpawnCommand())
				{
					// retrieve doc_ids that are linked to fileId
					cmd.CommandText = $"SELECT DISTINCT doc_id FROM document_files WHERE file_id='{fileId}'";

					var inQueryValue = "(";

					using (var reader = cmd.ExecuteReader())
					{
						while (reader.Read())
						{
							var linkedDocId = _dbHelper.SafeGetString(reader, 0);
							inQueryValue = $"{inQueryValue}'{linkedDocId}',";
						}
						reader.Close();
					}

					if (inQueryValue == "(")
					{
                        return 0;
					}

					inQueryValue = inQueryValue.Remove(inQueryValue.Length - 1) + ")";

					cmd.CommandText = "SELECT DISTINCT file_id FROM document_files WHERE doc_id IN " + inQueryValue;

					var belongedFileIds = new List<string>();

					using (var reader = cmd.ExecuteReader())
					{
						while (reader.Read())
						{
							var belongedFileId = _dbHelper.SafeGetString(reader, 0);
							belongedFileIds.Add(belongedFileId);
						}
						reader.Close();
					}

                    var copiedCount = 0;

					foreach (var belongedFileId in belongedFileIds)
					{
						var docFileId = Guid.NewGuid().ToString();
						docFileIds.Add(docFileId);

						var createDocumentFileResult = Post(new DLDocumentFile()
						{
							doc_id = docId,
							file_id = belongedFileId,
							doc_file_id = docFileId,
						}, true);

						if (createDocumentFileResult is BadRequestObjectResult)
						{
                            // Nothing do here
                            /*
							foreach (var id in docFileIds)
							{
								__deleteDocumentFile(id);
							}

							return copiedCount;
                            */
						}
                        else
                        {
                            copiedCount++;
                        }
					}

					return copiedCount;
				}
			}
			catch (Exception exception)
			{
				foreach (var id in docFileIds)
				{
					__deleteDocumentFile(id);
				}

				return 0;
			}
		}

		private void __deleteProjectDocument(string docId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = $"DELETE FROM project_documents WHERE doc_id='{docId}'";
				cmd.ExecuteNonQuery();
			}
		}

		private void __deleteFolderContent(string folderContentId, bool deleteRecord = true)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				var folderId = "";
				cmd.CommandText = $"SELECT folder_id FROM project_folder_contents WHERE folder_content_id='{folderContentId}'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						folderId = _dbHelper.SafeGetString(reader, 0);
					}
				}

				if (deleteRecord)
				{
					cmd.CommandText = $"DELETE FROM project_folder_contents WHERE folder_content_id='{folderContentId}'";
				}
				else
				{
					cmd.CommandText = "UPDATE project_folder_contents SET status=@status, edit_datetime=@edit_datetime WHERE folder_content_id='" + folderContentId + "'";
					cmd.Parameters.AddWithValue("status", "deleted");
					cmd.Parameters.AddWithValue("edit_datetime", DateTime.UtcNow);
				}
				cmd.ExecuteNonQuery();

				cmd.CommandText = "UPDATE project_folders SET folder_content_quantity = folder_content_quantity - 1 "
												+ $"WHERE folder_id='{folderId}';";
				cmd.ExecuteNonQuery();
			}
		}

		private void __deleteFile(string fileId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = $"DELETE FROM files WHERE file_id='{fileId}'";
				cmd.ExecuteNonQuery();
			}
		}

		private void __deleteDocumentFile(string docFileId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = $"DELETE FROM document_files WHERE doc_file_id='{docFileId}'";
				cmd.ExecuteNonQuery();
			}
		}

		private string __getRootFolderId(string project_id, string folder_type)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT folder_id FROM project_folders "
												+ $"WHERE project_id='{project_id}' "
												+ $"AND folder_type='{folder_type}' "
												+ "AND COALESCE(parent_folder_id, '') = '';";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						return _dbHelper.SafeGetString(reader, 0);
					}
					else
					{
						return null;
					}
				}
			}
		}

		private string __getFolderContentId(string folder_id, string doc_id)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT folder_content_id FROM project_folder_contents "
														+ $"WHERE folder_id='{folder_id}' AND doc_id='{doc_id}'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						return _dbHelper.SafeGetString(reader, 0);
					}
					else
					{
						return null;
					}
				}
			}
		}

		static public string __getRootFolderName(string folder_type)
		{
			var rootFolderName = "";

			switch (folder_type)
			{
				case "other_all":
					rootFolderName = "Other-All";
					break;
				case "other_current":
					rootFolderName = "Other-Current";
					break;
				case "plans_all":
					rootFolderName = "Plans-All";
					break;
				case "plans_current":
					rootFolderName = "Plans-Current";
					break;
				case "plans_comparison":
					rootFolderName = "Plans-Comparison";
					break;
				case "plans_raster":
					rootFolderName = "Plans-Raster";
					break;
				case "source_current":
					rootFolderName = "Source Files";
					break;
				case "source_history":
					rootFolderName = "Source Files-Submissions";
					break;
				case "specs_all":
					rootFolderName = "Specs-All";
					break;
				case "specs_current":
					rootFolderName = "Specs-Current";
					break;
				default:
					rootFolderName = "User-Defined";
					break;
			}

			return rootFolderName;
		}

		private bool __checkFolderContentExists(string folderId, string docId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT EXISTS (SELECT true FROM project_folder_contents WHERE status='active' AND folder_id='" + folderId + "' AND doc_id='" + docId + "')";
				return (bool)cmd.ExecuteScalar();
			}
		}

		private bool __isSourceFileSubmissionFolderEnabled(string project_id)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT setting_value FROM customer_settings LEFT JOIN projects ON projects.project_customer_id=customer_settings.customer_id "
						+ $"WHERE project_id='{project_id}' AND setting_id='source_file_submission_folder'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						var settingValue = _dbHelper.SafeGetString(reader, 0);

						return settingValue == "enabled";
					}
					else
					{
						return false;
					}
				}
			}
		}

		private List<Dictionary<string, object>> __GetFolderContentFromDocId(string docId)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT "
					+ "project_documents.project_id, project_folder_contents.folder_id, project_folder_contents.doc_id, "
					+ "project_folder_contents.folder_path, project_folder_contents.file_id, project_folders.folder_name "
					+ "FROM project_folder_contents "
					+ "LEFT JOIN project_documents ON project_documents.doc_id=project_folder_contents.doc_id "
					+ "LEFT JOIN project_folders ON project_folders.folder_id=project_folder_contents.folder_id "
					+ $"WHERE project_documents.doc_id = '{docId}'";
				//cmd.Parameters.AddWithValue("@doc_id", docId);

				var resultList = new List<Dictionary<string, object>>();
				using (var reader = cmd.ExecuteReader())
				{
					while (reader.Read())
					{
						resultList.Add(new Dictionary<string, object>
						{
							{ "project_id", _dbHelper.SafeGetString(reader, 0) },
							{ "folder_id", _dbHelper.SafeGetString(reader, 1) },
							{ "doc_id", _dbHelper.SafeGetString(reader, 2) },
							{ "folder_path", _dbHelper.SafeGetString(reader, 3) },
							{ "file_id", _dbHelper.SafeGetString(reader, 4) },
							{ "original_folder_name", _dbHelper.SafeGetString(reader, 5) },
							{ "new_folder_name", _dbHelper.SafeGetString(reader, 5) },
						});
					}
				}
				return resultList;
			}
		}

		private Dictionary<string, object> __getFolderContent(string folder_content_id)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = "SELECT project_documents.project_id, project_folder_contents.folder_id, project_folder_contents.doc_id, project_folder_contents.folder_path, project_folder_contents.file_id, project_folders.folder_name FROM project_folder_contents "
						+ "LEFT JOIN project_documents ON project_documents.doc_id=project_folder_contents.doc_id "
						+ "LEFT JOIN project_folders ON project_folders.folder_id=project_folder_contents.folder_id "
						+ $"WHERE folder_content_id='{folder_content_id}'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						return new Dictionary<string, object>
						{
							{ "project_id", _dbHelper.SafeGetString(reader, 0) },
							{ "folder_id", _dbHelper.SafeGetString(reader, 1) },
							{ "doc_id", _dbHelper.SafeGetString(reader, 2) },
							{ "folder_path", _dbHelper.SafeGetString(reader, 3) },
							{ "file_id", _dbHelper.SafeGetString(reader, 4) },
							{ "original_folder_name", _dbHelper.SafeGetString(reader, 5) },
							{ "new_folder_name", _dbHelper.SafeGetString(reader, 5) },
						};
					}
				}

				return null;
			}
		}

		private string __getProjectIdFromFolderId(string folder_id)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = $"SELECT project_id FROM project_folders WHERE folder_id='{folder_id}'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						return _dbHelper.SafeGetString(reader, 0);
					}
					else
					{
						return "";
					}
				}
			}
		}

		private string __getFolderNameFromFolderId(string folder_id)
		{
			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = $"SELECT folder_name FROM project_folders WHERE folder_id='{folder_id}'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						return _dbHelper.SafeGetString(reader, 0);
					}
					else
					{
						return "";
					}
				}
			}
		}

		private string __getSubmissionName(string submission_id)
		{
			if (string.IsNullOrEmpty(submission_id))
			{
				return "";
			}

			using (var cmd = _dbHelper.SpawnCommand())
			{
				cmd.CommandText = $"SELECT submission_name FROM project_submissions WHERE project_submission_id='{submission_id}'";

				using (var reader = cmd.ExecuteReader())
				{
					if (reader.Read())
					{
						return _dbHelper.SafeGetString(reader, 0);
					}
					else
					{
						return "";
					}
				}
			}
		}

        private string __getProjectCustomerId(string project_id)
        {
            using (var cmd = _dbHelper.SpawnCommand())
            {
                cmd.CommandText = $"SELECT project_customer_id FROM projects WHERE project_id='{project_id}'";

                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return _dbHelper.SafeGetString(reader, 0);
                    }
                }
            }

            return "";
        }

        [HttpPost]
        [Route("UpdateDocumentKeyAttributes")]
        public async Task<IActionResult> PostAsync(KeyAttributeUpdateRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.search_project_document_id))
                {
                    return BadRequest(new
                    {
                        status = "Please provide document id"
                    });
                }

                // Preload all related/necessary info
                var relatedInfo = _documentManagementService.GetInfoForKeyAttributeUpdate(_dbHelper, request.search_project_document_id);

                if (relatedInfo == null)
                {
                    return BadRequest(new
                    {
                        status = "Provided doc_id is invalid, failed to get info"
                    });
                }

                // #110 - Was a next_rev_id or previous_rev_id passed?
                if (string.IsNullOrEmpty(request.doc_prev_rev) && string.IsNullOrEmpty(request.doc_next_rev))
                {
                    // #100 - Did any of the unique key attributes for the document change?
                    if (string.IsNullOrEmpty(request.doc_number)
                        && string.IsNullOrEmpty(request.doc_pagenumber)
                        && string.IsNullOrEmpty(request.doc_subproject))
                    {
						await _ProcessNonKeyAttributeUpdate(request, relatedInfo);
					}
                    else
                    {
						// Update original revision chain
						await _ProcessOriginalRevisionChain(request, relatedInfo);

                        // #200 - Do the updated document key field(s) match an existing project_document?
                        // #300 
                        await __ProcessDuplicatedDocumentKeyAttributes(request, relatedInfo);
					}
                }
                else
                {
                    // #300 doc_next_rev or doc_prev_rev change => Should update the revision chain
                    await __ProcessDocumentRevisionAttributesChange(request, relatedInfo);
                }

                // #700 - Update App Transaction Log
                // DO SOMETHING HERE!!!

                return Ok(new
                {
                    status = "updated"
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    status = ex.Message
                });
            }
            finally
            {
                _dbHelper.CloseConnection();
            }
        }

		private async Task _ProcessNonKeyAttributeUpdate(KeyAttributeUpdateRequest request, Dictionary<string, string> relatedInfo)
        {
			// #620 - Same non-key attributes
			Post(new ProjectDocumentUpdateRequest()
			{
				search_project_document_id = request.search_project_document_id,
				display_name = request.display_name,
				doc_name = request.doc_name,
				doc_name_abbrv = request.doc_name_abbrv,
				doc_version = request.doc_version,
				doc_discipline = request.doc_discipline,
				doc_desc = request.doc_desc,
				process_status = request.process_status,
				status = request.status,
				doc_revision = request.doc_revision,
				doc_sequence = request.doc_sequence
			}, true);

			// #600 - doc_name or doc_rev change?
			if (!string.IsNullOrEmpty(request.doc_name) || !string.IsNullOrEmpty(request.doc_revision))
			{
				// #610 - Update Folder Transaction Log
				// todo: republish won't work in current system. be back later.
				//_documentManagementService.RecreateFolderTransactionLog(_dbHelper, request.search_project_document_id);

				// Republish
				await _documentManagementService.RepublishDocuments(_dbHelper, new List<string> { request.search_project_document_id }, relatedInfo);
			}
		}

		private async Task _ProcessOriginalRevisionChain(KeyAttributeUpdateRequest request, Dictionary<string, string> relatedInfo)
        {
			// Update existing revision chain
			var previousDocId = _documentManagementService.GetPreviousRevisionDocId(_dbHelper, request.search_project_document_id);
			var nextDocId = _documentManagementService.GetNextRevisionDocId(_dbHelper, request.search_project_document_id);

			Post(new ProjectDocumentUpdateRequest()
			{
				search_project_document_id = request.search_project_document_id,
				doc_next_rev = "NULL",
			}, true);

			if (!string.IsNullOrEmpty(previousDocId))
			{
				if (string.IsNullOrEmpty(nextDocId))
				{
					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = previousDocId,
						doc_next_rev = "NULL",
					}, true);
				}
				else
				{
					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = previousDocId,
						doc_next_rev = nextDocId,
					}, true);
				}
			}

			// Update next revisions
			if (!string.IsNullOrEmpty(nextDocId))
            {
				var isCounterBased = relatedInfo["revisioning_type"] != "Submission Date";
				var timezone = relatedInfo["customer_timezone"];
				var revisions = _documentManagementService.GetDocumentRevisions(_dbHelper, nextDocId);
				var nextDocIndex = revisions.FindIndex(revision => revision["doc_id"] == nextDocId);

				if (isCounterBased)
				{
					for (var idx = nextDocIndex; idx < revisions.Count; idx++)
					{
						Post(new ProjectDocumentUpdateRequest()
						{
							search_project_document_id = revisions[idx]["doc_id"],
							doc_revision = idx == 0 ? "NULL" : idx.ToString("D2"),
						}, true);
					}
				}
				else
				{
					var previousDocs = revisions.GetRange(0, nextDocIndex);

					for (var idx = nextDocIndex; idx < revisions.Count; idx++)
					{
						Post(new ProjectDocumentUpdateRequest()
						{
							search_project_document_id = revisions[idx]["doc_id"],
							doc_revision = _documentManagementService.CalculateDocRevisionForSubmissionDate(relatedInfo["submission_datetime"], timezone, previousDocs),
						}, true);

						previousDocs.Add(revisions[idx]);
					}
				}

				for (var idx = nextDocIndex; idx < revisions.Count; idx++)
                {
					if (idx > 0)
                    {
						await _documentManagementService.CreateComparison(_dbHelper, revisions[idx]["doc_id"], revisions[idx - 1]["doc_id"], relatedInfo);
					}
				}

				var docIds = revisions.GetRange(nextDocIndex, revisions.Count - nextDocIndex).Select(document => document["doc_id"]).ToList<string>();
				await _documentManagementService.RepublishDocuments(_dbHelper, docIds, relatedInfo);
			}

			// If this doc was latest, remove from current plan and create new folder content record for previous revision
			if (relatedInfo["current_plans_folder"] == "enabled" && string.IsNullOrEmpty(nextDocId))
			{
				var folderContentRecord = _documentManagementService.GetCurrentPlanFolderContentRecord(_dbHelper, request.search_project_document_id);

				if (folderContentRecord != null)
				{
					Post(new DLFolderContentDeleteRequest()
					{
						folder_content_id = folderContentRecord["folder_content_id"],
					}, true);

					await _documentManagementService.UnpublishDocumentFromCurrentPlan(_dbHelper, request.search_project_document_id, relatedInfo);

					if (previousDocId != null)
					{
						var previousDocFile = _documentManagementService.GetSourceFileInfo(_dbHelper, previousDocId);

						if (previousDocFile != null)
						{
							Post(new DLFolderContent()
							{
								doc_id = previousDocId,
								file_id = previousDocFile["file_id"],
								folder_path = folderContentRecord["folder_path"],
								folder_id = folderContentRecord["folder_id"],
								folder_original_filename = previousDocFile["file_original_filename"],
								project_id = relatedInfo["project_id"],
							}, true);

							await _documentManagementService.PublishDocumentToCurrentPlan(_dbHelper, previousDocId, relatedInfo);
						}
					}
				}
			}
		}

        private async Task __ProcessDuplicatedDocumentKeyAttributes(
			KeyAttributeUpdateRequest request,
            Dictionary<string, string> relatedInfo)
		{
			// Update non-key attributes first
			Post(new ProjectDocumentUpdateRequest()
			{
				search_project_document_id = request.search_project_document_id,
				display_name = request.display_name,
				doc_name = request.doc_name,
				doc_name_abbrv = request.doc_name_abbrv,
				doc_version = request.doc_version,
				doc_discipline = request.doc_discipline,
				doc_desc = request.doc_desc,
				process_status = request.process_status,
				status = request.status,
				doc_sequence = request.doc_sequence
			}, true);

			// Find match
			var matchedDocuments = _documentManagementService.RetrieveMatchedDocumentsWithKeyAttributes(
							_dbHelper,
							request.search_project_document_id,
							relatedInfo["project_id"],
							request.doc_number,
							request.doc_pagenumber,
							request.doc_subproject);

			// If there is no match ...
			if (matchedDocuments.Count == 0)
            {
				Post(new ProjectDocumentUpdateRequest()
				{
					search_project_document_id = request.search_project_document_id,
					doc_revision = "NULL",
					doc_next_rev = "NULL",
					doc_number = request.doc_number,
					doc_pagenumber = request.doc_pagenumber,
					doc_subproject = request.doc_subproject
				}, true);

				if (relatedInfo["current_plans_folder"] == "enabled")
				{
					var docInfo = _documentManagementService.GetDocumentInfo(_dbHelper, request.search_project_document_id);

					Post(new DLFolderContent()
					{
						doc_id = request.search_project_document_id,
						file_id = relatedInfo["file_id"],
						folder_path = relatedInfo["discipline_plans_folder"].Contains("current_plans") ? _documentManagementService.GetDisciplineFolderName(docInfo["doc_number"]) : null,
						folder_type = "plans_current",
						folder_original_filename = relatedInfo["file_original_filename"],
						project_id = relatedInfo["project_id"],
					}, true);

					await _documentManagementService.PublishDocumentToCurrentPlan(_dbHelper, request.search_project_document_id, relatedInfo);
				}
			}
			else
			{
				// Update document record
				Post(new ProjectDocumentUpdateRequest()
				{
					search_project_document_id = request.search_project_document_id,
					doc_number = request.doc_number,
					doc_pagenumber = request.doc_pagenumber,
					doc_subproject = request.doc_subproject
				}, true);

				// Determine where to insert the current document
				var index = 0;
				var currentDocumentDate = relatedInfo["create_datetime"];

				if (!string.IsNullOrEmpty(relatedInfo["parent_original_modified_datetime"]))
				{
					currentDocumentDate = relatedInfo["parent_original_modified_datetime"];
				}
				else if (!string.IsNullOrEmpty(relatedInfo["file_original_modified_datetime"]))
				{
					currentDocumentDate = relatedInfo["file_original_modified_datetime"];
				}

				while (index < matchedDocuments.Count)
				{
					var matchedDocument = matchedDocuments[index];
					var matchedDocumentDate = matchedDocument["create_datetime"];

					if (!string.IsNullOrEmpty(matchedDocument["parent_original_modified_datetime"]))
					{
						matchedDocumentDate = matchedDocument["parent_original_modified_datetime"];
					}
					else if (!string.IsNullOrEmpty(matchedDocument["file_original_modified_datetime"]))
					{
						matchedDocumentDate = matchedDocument["file_original_modified_datetime"];
					}

					if (currentDocumentDate.CompareTo(matchedDocumentDate) > 0)
					{
						index++;
					}
					else
					{
						break;
					}
				}

				// So it should be inserted before [index], now update revision chain
				var isCounterBased = relatedInfo["revisioning_type"] != "Submission Date";
				var timezone = relatedInfo["customer_timezone"];
				var calculatedRevision = "";

				if (index == matchedDocuments.Count)
				{
					// Become latest
					// No need to update previous doc revision, just calculate current one and link to the last one
					if (isCounterBased)
					{
						calculatedRevision = (StringHelper.ConvertToInteger(matchedDocuments[index - 1]["doc_revision"]) + 1).ToString("D2");
					}
					else
					{
						calculatedRevision = _documentManagementService.CalculateDocRevisionForSubmissionDate(relatedInfo["submission_datetime"], timezone, matchedDocuments);
					}

					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = matchedDocuments[index - 1]["doc_id"],
						doc_next_rev = request.search_project_document_id,
					}, true);

					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = request.search_project_document_id,
						doc_revision = calculatedRevision,
					}, true);


					// Update folder content in current plans folder
					if (relatedInfo["current_plans_folder"] == "enabled")
					{
						var folderContentRecord = _documentManagementService.GetCurrentPlanFolderContentRecord(_dbHelper, matchedDocuments[index - 1]["doc_id"]);

						if (folderContentRecord != null)
						{
							Post(new DLFolderContentDeleteRequest()
							{
								folder_content_id = folderContentRecord["folder_content_id"],
							}, true);

							Post(new DLFolderContent()
							{
								doc_id = request.search_project_document_id,
								file_id = relatedInfo["file_id"],
								folder_path = folderContentRecord["folder_path"],
								folder_id = folderContentRecord["folder_id"],
								folder_original_filename = relatedInfo["file_original_filename"],
								project_id = relatedInfo["project_id"],
							}, true);

							await _documentManagementService.UnpublishDocumentFromCurrentPlan(_dbHelper, matchedDocuments[index - 1]["doc_id"], relatedInfo);
							await _documentManagementService.PublishDocumentToCurrentPlan(_dbHelper, request.search_project_document_id, relatedInfo);
						}
					}

					// Create new comparison
					await _documentManagementService.CreateComparison(_dbHelper, request.search_project_document_id, matchedDocuments[index - 1]["doc_id"], relatedInfo);
				}
				else if (index == 0)
				{
					// Become first
					// Need to update all doc revisions, link to first doc
					if (isCounterBased)
					{
						foreach (var document in matchedDocuments)
						{
							var newDocRevision = (StringHelper.ConvertToInteger(document["doc_revision"]) + 1).ToString("D2");

							Post(new ProjectDocumentUpdateRequest()
							{
								search_project_document_id = document["doc_id"],
								doc_revision = newDocRevision,
							}, true);
						}
					}
					else
					{
						var previousDocuments = new List<Dictionary<string, string>> { relatedInfo };

						for (var idx = 0; idx < matchedDocuments.Count; idx++)
						{
							var newRevision = _documentManagementService.CalculateDocRevisionForSubmissionDate(matchedDocuments[idx]["submission_datetime"], timezone, previousDocuments);

							Post(new ProjectDocumentUpdateRequest()
							{
								search_project_document_id = matchedDocuments[idx]["doc_id"],
								doc_revision = newRevision,
							}, true);

							previousDocuments.Add(matchedDocuments[idx]);
						}
					}

					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = request.search_project_document_id,
						doc_next_rev = matchedDocuments[0]["doc_id"],
						doc_revision = "NULL",
					}, true);

					// No need to update current plans folder

					// Create comparison
					await _documentManagementService.CreateComparison(_dbHelper, matchedDocuments[0]["doc_id"], request.search_project_document_id, relatedInfo);
				}
				else
				{
					// In the middle
					// Need to update "after" doc revisions, break current link and insert this doc
					if (isCounterBased)
					{
						Post(new ProjectDocumentUpdateRequest()
						{
							search_project_document_id = request.search_project_document_id,
							doc_revision = (StringHelper.ConvertToInteger(matchedDocuments[index - 1]["doc_revision"]) + 1).ToString("D2"),
						}, true);

						for (var idx = index; idx < matchedDocuments.Count; idx++)
						{
							Post(new ProjectDocumentUpdateRequest()
							{
								search_project_document_id = matchedDocuments[idx]["doc_id"],
								doc_revision = (StringHelper.ConvertToInteger(matchedDocuments[idx]["doc_revision"]) + 1).ToString("D2"),
							}, true);
						}
					}
					else
					{
						var previousDocs = matchedDocuments.GetRange(0, index);

						Post(new ProjectDocumentUpdateRequest()
						{
							search_project_document_id = request.search_project_document_id,
							doc_revision = _documentManagementService.CalculateDocRevisionForSubmissionDate(relatedInfo["submission_datetime"], timezone, previousDocs),
						}, true);

						previousDocs.Add(relatedInfo);

						for (var idx = index; idx < matchedDocuments.Count; idx++)
						{
							Post(new ProjectDocumentUpdateRequest()
							{
								search_project_document_id = matchedDocuments[idx]["doc_id"],
								doc_revision = _documentManagementService.CalculateDocRevisionForSubmissionDate(relatedInfo["submission_datetime"], timezone, previousDocs),
							}, true);

							previousDocs.Add(matchedDocuments[idx]);
						}
					}

					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = request.search_project_document_id,
						doc_next_rev = matchedDocuments[index]["doc_id"],
					}, true);

					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = matchedDocuments[index - 1]["doc_id"],
						doc_next_rev = request.search_project_document_id,
					}, true);

					// Create two comparison
					await _documentManagementService.CreateComparison(_dbHelper, matchedDocuments[index]["doc_id"], request.search_project_document_id, relatedInfo);
					await _documentManagementService.CreateComparison(_dbHelper, request.search_project_document_id, matchedDocuments[index - 1]["doc_id"], relatedInfo);
				}
			}

			// Republish
			var docIds = matchedDocuments.Select(document => document["doc_id"]).ToList<string>();
			docIds.Add(request.search_project_document_id);

			await _documentManagementService.RepublishDocuments(_dbHelper, docIds, relatedInfo);

			//_documentManagementService.RecreateFolderTransactionLog(_dbHelper, request.search_project_document_id);

			//foreach (var document in matchedDocuments)
			//{
			//    _documentManagementService.RecreateFolderTransactionLog(_dbHelper, document["doc_id"]);
			//}
		}

        private async Task __ProcessDocumentRevisionAttributesChange(KeyAttributeUpdateRequest request, Dictionary<string, string> relatedInfo)
        {
			// Update non key attributes first
			Post(new ProjectDocumentUpdateRequest()
			{
				search_project_document_id = request.search_project_document_id,
				display_name = request.display_name,
				doc_name = request.doc_name,
				doc_name_abbrv = request.doc_name_abbrv,
				doc_version = request.doc_version,
				doc_discipline = request.doc_discipline,
				doc_desc = request.doc_desc,
				process_status = request.process_status,
				status = request.status,
				doc_sequence = request.doc_sequence
			}, true);

			// Relink current doc's prev and next rev
			var previousDocId = _documentManagementService.GetPreviousRevisionDocId(_dbHelper, request.search_project_document_id);
			var nextDocId = _documentManagementService.GetNextRevisionDocId(_dbHelper, request.search_project_document_id);

			if (string.IsNullOrEmpty(previousDocId) && string.IsNullOrEmpty(nextDocId))
            {
				// There's no revision chain. Bad request
				return;
            }

			if (!string.IsNullOrEmpty(previousDocId))
			{
				Post(new ProjectDocumentUpdateRequest()
				{
					search_project_document_id = previousDocId,
					doc_next_rev = string.IsNullOrEmpty(nextDocId) ? "NULL" : nextDocId,
				}, true);

				// create new comparison
				if (!string.IsNullOrEmpty(nextDocId))
                {
					await _documentManagementService.CreateComparison(_dbHelper, nextDocId, previousDocId, relatedInfo);
				}
			}

			List<Dictionary<string, string>> revisions = _documentManagementService.GetDocumentRevisions(_dbHelper, string.IsNullOrEmpty(previousDocId) ? nextDocId : previousDocId);

			if (!string.IsNullOrEmpty(request.doc_prev_rev))
            {
				var newPrevRev = revisions.Find(revision =>
				{
					return revision["doc_id"] == request.doc_prev_rev;
				});

				if (newPrevRev == null)
                {
					return;
                }

				var nextDocIdOfNewPrevRev = _documentManagementService.GetNextRevisionDocId(_dbHelper, newPrevRev["doc_id"]);

				Post(new ProjectDocumentUpdateRequest()
				{
					search_project_document_id = request.doc_prev_rev,
					doc_next_rev = request.search_project_document_id,
				}, true);

				Post(new ProjectDocumentUpdateRequest()
				{
					search_project_document_id = request.search_project_document_id,
					doc_next_rev = string.IsNullOrEmpty(nextDocIdOfNewPrevRev) ? "NULL" : nextDocIdOfNewPrevRev,
				}, true);

				// Create comparison
				await _documentManagementService.CreateComparison(_dbHelper, request.search_project_document_id, request.doc_prev_rev, relatedInfo);

				if (!string.IsNullOrEmpty(nextDocIdOfNewPrevRev))
                {
					await _documentManagementService.CreateComparison(_dbHelper, nextDocIdOfNewPrevRev, request.search_project_document_id, relatedInfo);
                }

				// Update folder content
				if (relatedInfo["current_plans_folder"] == "enabled" && string.IsNullOrEmpty(nextDocIdOfNewPrevRev))
                {
					// Update folder content in current plans folder 
					var folderContentRecord = _documentManagementService.GetCurrentPlanFolderContentRecord(_dbHelper, request.doc_prev_rev);

					if (folderContentRecord != null)
					{
						Post(new DLFolderContentDeleteRequest()
						{
							folder_content_id = folderContentRecord["folder_content_id"],
						}, true);

						await _documentManagementService.UnpublishDocumentFromCurrentPlan(_dbHelper, request.doc_prev_rev, relatedInfo);

						Post(new DLFolderContent()
						{
							doc_id = request.search_project_document_id,
							file_id = relatedInfo["file_id"],
							folder_path = folderContentRecord["folder_path"],
							folder_id = folderContentRecord["folder_id"],
							folder_original_filename = relatedInfo["file_original_filename"],
							project_id = relatedInfo["project_id"],
						}, true);

						await _documentManagementService.PublishDocumentToCurrentPlan(_dbHelper, request.search_project_document_id, relatedInfo);
					}
				}
            }
			else if (!string.IsNullOrEmpty(request.doc_next_rev))
            {
				var newNextRev = revisions.Find(revision =>
				{
					return revision["doc_id"] == request.doc_next_rev;
				});

				if (newNextRev == null)
				{
					return;
				}

				var prevDocIdOfNewNextRev = _documentManagementService.GetPreviousRevisionDocId(_dbHelper, newNextRev["doc_id"]);

				Post(new ProjectDocumentUpdateRequest()
				{
					search_project_document_id = request.search_project_document_id,
					doc_next_rev = request.doc_next_rev,
				}, true);

				if (!string.IsNullOrEmpty(prevDocIdOfNewNextRev))
                {
					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = prevDocIdOfNewNextRev,
						doc_next_rev = request.search_project_document_id,
					}, true);
				}

				// Create comparison
				await _documentManagementService.CreateComparison(_dbHelper, request.doc_next_rev, request.search_project_document_id, relatedInfo);

				if (!string.IsNullOrEmpty(prevDocIdOfNewNextRev))
                {
					await _documentManagementService.CreateComparison(_dbHelper, request.search_project_document_id, prevDocIdOfNewNextRev, relatedInfo);
                }

				// Update folder content
				if (relatedInfo["current_plans_folder"] == "enabled" && string.IsNullOrEmpty(nextDocId))
                {
					// This was originally latest doc, so should remove it from folder content and publish new one
					var folderContentRecord = _documentManagementService.GetCurrentPlanFolderContentRecord(_dbHelper, request.search_project_document_id);

					if (folderContentRecord != null)
					{
						Post(new DLFolderContentDeleteRequest()
						{
							folder_content_id = folderContentRecord["folder_content_id"],
						}, true);

						await _documentManagementService.UnpublishDocumentFromCurrentPlan(_dbHelper, request.search_project_document_id, relatedInfo);

						var nextDocFile = _documentManagementService.GetSourceFileInfo(_dbHelper, request.doc_next_rev);

						Post(new DLFolderContent()
						{
							doc_id = request.doc_next_rev,
							file_id = nextDocFile["file_id"],
							folder_path = folderContentRecord["folder_path"],
							folder_id = folderContentRecord["folder_id"],
							folder_original_filename = nextDocFile["file_original_filename"],
							project_id = relatedInfo["project_id"],
						}, true);

						await _documentManagementService.PublishDocumentToCurrentPlan(_dbHelper, request.doc_next_rev, relatedInfo);
					}
				}
			}

			// So revision chain updated ... now recalculate revisions
			var newRevisionChain = _documentManagementService.GetDocumentRevisions(_dbHelper, request.search_project_document_id);
			var isCounterBased = relatedInfo["revisioning_type"] != "Submission Date";
			var timezone = relatedInfo["customer_timezone"];

			if (isCounterBased)
            {
				for (var index = 0; index < newRevisionChain.Count; index++)
                {
					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = newRevisionChain[index]["doc_id"],
						doc_revision = index == 0 ? "NULL" : index.ToString("D2"),
					}, true);
				}
            }
			else
            {
				for (var index = 0; index < newRevisionChain.Count; index++)
				{
					var previousDocs = newRevisionChain.GetRange(0, index);

					Post(new ProjectDocumentUpdateRequest()
					{
						search_project_document_id = newRevisionChain[index]["doc_id"],
						doc_revision = _documentManagementService.CalculateDocRevisionForSubmissionDate(newRevisionChain[index]["submission_datetime"], timezone, previousDocs),
					}, true);
				}
			}

			// Republish
			var docIds = newRevisionChain.Select(document => document["doc_id"]).ToList();
			await _documentManagementService.RepublishDocuments(_dbHelper, docIds, relatedInfo);

			//foreach (var document in newRevisionChain)
   //         {
   //             _documentManagementService.RecreateFolderTransactionLog(_dbHelper, document["doc_id"]);
   //         }
		}
    }
}
